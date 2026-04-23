using System.Threading.Channels;
using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Services.Scanning;

internal sealed record LibraryWatchTarget(int Id, string Name, string Path);

internal enum LibraryWatchEventKind
{
    Upsert,
    Delete,
    Move,
    Overflow,
}

internal sealed record LibraryWatchEvent(
    LibraryWatchEventKind Kind,
    string RelativePath,
    string? OldRelativePath,
    bool IsDirectory,
    long Sequence);

internal static class LibraryWatchPathHelper
{
    public static bool TryGetRelativePath(LibraryWatchTarget library, string fullPath, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        try
        {
            var libraryPath = Path.GetFullPath(library.Path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedFullPath = Path.GetFullPath(fullPath);
            if (!normalizedFullPath.StartsWith(libraryPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    normalizedFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    library.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(library.Path, normalizedFullPath);
            return !string.IsNullOrWhiteSpace(relativePath)
                && !relativePath.StartsWith("..", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSupportedFile(string fullPath)
        => SupportedMedia.IsSupported(Path.GetExtension(fullPath));
}

public sealed class LibraryWatchSessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILibrarySyncProcessor _librarySyncProcessor;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _deleteGracePeriod;

    public LibraryWatchSessionFactory(
        IServiceScopeFactory scopeFactory,
        ILibrarySyncProcessor librarySyncProcessor,
        IFileIdentityResolver fileIdentityResolver,
        ILoggerFactory loggerFactory,
        IOptions<DamebooruConfig> options)
    {
        _scopeFactory = scopeFactory;
        _librarySyncProcessor = librarySyncProcessor;
        _fileIdentityResolver = fileIdentityResolver;
        _loggerFactory = loggerFactory;
        _debounceDelay = TimeSpan.FromMilliseconds(Math.Max(100, options.Value.Scanner.WatcherDebounceMs));
        _deleteGracePeriod = TimeSpan.FromMilliseconds(Math.Max(options.Value.Scanner.WatcherDebounceMs * 2, 3000));
    }

    internal LibraryWatchSession Create(LibraryWatchTarget library)
        => new(
            library,
            _scopeFactory,
            _librarySyncProcessor,
            _fileIdentityResolver,
            _loggerFactory.CreateLogger<LibraryWatchSession>(),
            _debounceDelay,
            _deleteGracePeriod);
}

internal sealed class LibraryWatchSession
{
    private static readonly TimeSpan MaterializeRetryDelay = TimeSpan.FromMilliseconds(250);
    private const int MaterializeRetryCount = 8;

    private sealed record PendingDeleteCandidate(
        string RelativePath,
        FileIdentity? Identity,
        bool IsDirectory,
        DateTime ExpiresAtUtc,
        long Sequence);

    private sealed record PendingDirectoryCreateCandidate(
        string RelativePath,
        DateTime ExpiresAtUtc,
        long Sequence);

    private sealed record PendingLibraryOperation(
        LibraryWatchEventKind Kind,
        string RelativePath,
        string? OldRelativePath,
        bool IsDirectory,
        long Sequence);

    private readonly LibraryWatchTarget _library;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILibrarySyncProcessor _librarySyncProcessor;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly ILogger<LibraryWatchSession> _logger;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _deleteGracePeriod;

    public LibraryWatchSession(
        LibraryWatchTarget library,
        IServiceScopeFactory scopeFactory,
        ILibrarySyncProcessor librarySyncProcessor,
        IFileIdentityResolver fileIdentityResolver,
        ILogger<LibraryWatchSession> logger,
        TimeSpan debounceDelay,
        TimeSpan deleteGracePeriod)
    {
        _library = library;
        _scopeFactory = scopeFactory;
        _librarySyncProcessor = librarySyncProcessor;
        _fileIdentityResolver = fileIdentityResolver;
        _logger = logger;
        _debounceDelay = debounceDelay;
        _deleteGracePeriod = deleteGracePeriod;
    }

    public async Task ProcessAsync(ChannelReader<LibraryWatchEvent> reader, CancellationToken cancellationToken)
    {
        var pendingDeletes = new List<PendingDeleteCandidate>();
        var pendingDirectoryCreates = new List<PendingDirectoryCreateCandidate>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            var expiryTask = HasPendingExpirations(pendingDeletes, pendingDirectoryCreates)
                ? Task.Delay(GetNextFlushDelay(pendingDeletes, pendingDirectoryCreates), cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var completedTask = await Task.WhenAny(waitTask, expiryTask);
            if (completedTask == expiryTask)
            {
                await FlushExpiredDeletesAsync(pendingDeletes, cancellationToken);
                FlushExpiredDirectoryCreates(pendingDirectoryCreates);
                continue;
            }

            if (!await waitTask)
            {
                break;
            }

            var batch = new List<LibraryWatchEvent>();
            while (reader.TryRead(out var watchEvent))
            {
                batch.Add(watchEvent);
            }

            while (true)
            {
                var delayTask = Task.Delay(_debounceDelay, cancellationToken);
                var debounceWaitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(delayTask, debounceWaitTask);
                if (completed == delayTask)
                {
                    break;
                }

                if (!await debounceWaitTask)
                {
                    break;
                }

                while (reader.TryRead(out var watchEvent))
                {
                    batch.Add(watchEvent);
                }
            }

            foreach (var operation in CompactBatch(batch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FlushExpiredDeletesAsync(pendingDeletes, cancellationToken);
                FlushExpiredDirectoryCreates(pendingDirectoryCreates);
                _logger.LogDebug(
                    "Watcher compacted operation for library {Library}: {Kind} {EntryType} {OldPath} -> {Path}",
                    _library.Name,
                    operation.Kind,
                    operation.IsDirectory ? "directory" : "file",
                    operation.OldRelativePath ?? "-",
                    operation.RelativePath);
                if (operation.IsDirectory && operation.Kind == LibraryWatchEventKind.Move)
                {
                    _logger.LogInformation(
                        "Watcher compacted directory move for library {Library}: {OldPath} -> {NewPath}",
                        _library.Name,
                        operation.OldRelativePath,
                        operation.RelativePath);
                }
                await ProcessOperationAsync(operation, pendingDeletes, pendingDirectoryCreates, cancellationToken);
            }

            await FlushExpiredDeletesAsync(pendingDeletes, cancellationToken);
            FlushExpiredDirectoryCreates(pendingDirectoryCreates);
        }

        await FlushAllDeletesAsync(pendingDeletes, cancellationToken);
        pendingDirectoryCreates.Clear();
    }

    private async Task ProcessOperationAsync(
        PendingLibraryOperation operation,
        List<PendingDeleteCandidate> pendingDeletes,
        List<PendingDirectoryCreateCandidate> pendingDirectoryCreates,
        CancellationToken cancellationToken)
    {
        var libraryEntity = ToLibraryEntity();

        switch (operation.Kind)
        {
            case LibraryWatchEventKind.Upsert:
            {
                if (operation.IsDirectory)
                {
                    var pendingDirectoryDelete = TryMatchPendingDirectoryDelete(pendingDeletes, operation.RelativePath);
                    if (pendingDirectoryDelete != null)
                    {
                        _logger.LogInformation(
                            "Watcher inferred directory move for library {Library}: {OldPath} -> {NewPath}",
                            _library.Name,
                            pendingDirectoryDelete.RelativePath,
                            operation.RelativePath);
                        await _librarySyncProcessor.ProcessMovedDirectoryAsync(
                            libraryEntity,
                            pendingDirectoryDelete.RelativePath,
                            operation.RelativePath,
                            cancellationToken);
                    }
                    else
                    {
                        StagePendingDirectoryCreate(operation.RelativePath, operation.Sequence, pendingDirectoryCreates);
                    }

                    return;
                }

                var item = await CreateMediaSourceItemAsync(operation.RelativePath, cancellationToken);
                if (item == null)
                {
                    _logger.LogWarning(
                        "Watcher upsert could not materialize file for library {Library}: {Path}",
                        _library.Name,
                        operation.RelativePath);
                    return;
                }

                var identity = _fileIdentityResolver.TryResolve(item.FullPath);
                var pendingDelete = TryMatchPendingDelete(pendingDeletes, identity);
                if (pendingDelete != null)
                {
                    _logger.LogDebug(
                        "Watcher inferred move for library {Library} by identity: {OldPath} -> {NewPath}",
                        _library.Name,
                        pendingDelete.RelativePath,
                        operation.RelativePath);
                    await _librarySyncProcessor.ProcessMovedFileAsync(libraryEntity, pendingDelete.RelativePath, item, cancellationToken);
                    break;
                }

                _logger.LogDebug("Watcher invoking changed-file processing for library {Library}: {Path}", _library.Name, operation.RelativePath);
                await _librarySyncProcessor.ProcessChangedFileAsync(libraryEntity, item, cancellationToken);
                break;
            }
            case LibraryWatchEventKind.Delete:
                if (operation.IsDirectory || await IsTrackedDirectoryPrefixAsync(operation.RelativePath, cancellationToken))
                {
                    var pendingDirectoryCreate = TryMatchPendingDirectoryCreate(pendingDirectoryCreates, operation.RelativePath);
                    if (pendingDirectoryCreate != null)
                    {
                        _logger.LogInformation(
                            "Watcher inferred directory move for library {Library}: {OldPath} -> {NewPath}",
                            _library.Name,
                            operation.RelativePath,
                            pendingDirectoryCreate.RelativePath);
                        await _librarySyncProcessor.ProcessMovedDirectoryAsync(
                            libraryEntity,
                            operation.RelativePath,
                            pendingDirectoryCreate.RelativePath,
                            cancellationToken);
                        break;
                    }

                    await StagePendingDeleteAsync(operation, pendingDeletes, isDirectory: true, cancellationToken);
                    break;
                }

                await StagePendingDeleteAsync(operation, pendingDeletes, isDirectory: false, cancellationToken);
                break;
            case LibraryWatchEventKind.Move:
            {
                if (string.IsNullOrWhiteSpace(operation.OldRelativePath))
                {
                    return;
                }

                if (operation.IsDirectory)
                {
                    RemovePendingDirectoryCreate(pendingDirectoryCreates, operation.OldRelativePath);
                    RemovePendingDirectoryCreate(pendingDirectoryCreates, operation.RelativePath);
                    _logger.LogInformation(
                        "Watcher invoking moved-directory processing for library {Library}: {OldPath} -> {NewPath}",
                        _library.Name,
                        operation.OldRelativePath,
                        operation.RelativePath);
                    await _librarySyncProcessor.ProcessMovedDirectoryAsync(
                        libraryEntity,
                        operation.OldRelativePath,
                        operation.RelativePath,
                        cancellationToken);
                    break;
                }

                var item = await CreateMediaSourceItemAsync(operation.RelativePath, cancellationToken);
                if (item == null)
                {
                    _logger.LogWarning(
                        "Skipping move processing for {OldPath} -> {NewPath} in library {Library} because the destination file could not be materialized after retries.",
                        operation.OldRelativePath,
                        operation.RelativePath,
                        _library.Name);
                    return;
                }

                RemovePendingDelete(pendingDeletes, operation.OldRelativePath);
                RemovePendingDelete(pendingDeletes, operation.RelativePath);
                _logger.LogDebug(
                    "Watcher invoking moved-file processing for library {Library}: {OldPath} -> {NewPath}",
                    _library.Name,
                    operation.OldRelativePath,
                    operation.RelativePath);
                await _librarySyncProcessor.ProcessMovedFileAsync(libraryEntity, operation.OldRelativePath, item, cancellationToken);
                break;
            }
            case LibraryWatchEventKind.Overflow:
                break;
        }
    }

    private static List<PendingLibraryOperation> CompactBatch(List<LibraryWatchEvent> batch)
    {
        var map = new Dictionary<string, PendingLibraryOperation>(StringComparer.OrdinalIgnoreCase);

        foreach (var watchEvent in batch)
        {
            switch (watchEvent.Kind)
            {
                case LibraryWatchEventKind.Upsert:
                    if (!map.TryGetValue(watchEvent.RelativePath, out var existing)
                        || existing.Kind != LibraryWatchEventKind.Move)
                    {
                        map[watchEvent.RelativePath] = new PendingLibraryOperation(
                            LibraryWatchEventKind.Upsert,
                            watchEvent.RelativePath,
                            null,
                            watchEvent.IsDirectory,
                            watchEvent.Sequence);
                    }
                    break;

                case LibraryWatchEventKind.Delete:
                    if (map.TryGetValue(watchEvent.RelativePath, out var deleteExisting)
                        && deleteExisting.Kind == LibraryWatchEventKind.Move
                        && !string.IsNullOrWhiteSpace(deleteExisting.OldRelativePath))
                    {
                        map.Remove(watchEvent.RelativePath);
                        map[deleteExisting.OldRelativePath] = new PendingLibraryOperation(
                            LibraryWatchEventKind.Delete,
                            deleteExisting.OldRelativePath,
                            null,
                            deleteExisting.IsDirectory,
                            watchEvent.Sequence);
                    }
                    else
                    {
                        map[watchEvent.RelativePath] = new PendingLibraryOperation(
                            LibraryWatchEventKind.Delete,
                            watchEvent.RelativePath,
                            null,
                            watchEvent.IsDirectory,
                            watchEvent.Sequence);
                    }
                    break;

                case LibraryWatchEventKind.Move:
                    if (string.IsNullOrWhiteSpace(watchEvent.OldRelativePath))
                    {
                        break;
                    }

                    if (map.TryGetValue(watchEvent.OldRelativePath, out var oldExisting))
                    {
                        map.Remove(watchEvent.OldRelativePath);
                        if (oldExisting.Kind == LibraryWatchEventKind.Move
                            && !string.IsNullOrWhiteSpace(oldExisting.OldRelativePath))
                        {
                            map[watchEvent.RelativePath] = new PendingLibraryOperation(
                                LibraryWatchEventKind.Move,
                                watchEvent.RelativePath,
                                oldExisting.OldRelativePath,
                                watchEvent.IsDirectory,
                                oldExisting.Sequence);
                            break;
                        }

                        if (oldExisting.Kind == LibraryWatchEventKind.Upsert)
                        {
                            map[watchEvent.RelativePath] = new PendingLibraryOperation(
                                LibraryWatchEventKind.Upsert,
                                watchEvent.RelativePath,
                                null,
                                watchEvent.IsDirectory,
                                oldExisting.Sequence);
                            break;
                        }
                    }

                    map.Remove(watchEvent.RelativePath);
                    map[watchEvent.RelativePath] = new PendingLibraryOperation(
                        LibraryWatchEventKind.Move,
                        watchEvent.RelativePath,
                        watchEvent.OldRelativePath,
                        watchEvent.IsDirectory,
                        watchEvent.Sequence);
                    break;

                case LibraryWatchEventKind.Overflow:
                    map[$"__overflow__{watchEvent.Sequence}"] = new PendingLibraryOperation(
                        LibraryWatchEventKind.Overflow,
                        string.Empty,
                        null,
                        false,
                        watchEvent.Sequence);
                    break;
            }
        }

        return map.Values
            .OrderBy(op => op.Sequence)
            .ToList();
    }

    private async Task StagePendingDeleteAsync(
        PendingLibraryOperation operation,
        List<PendingDeleteCandidate> pendingDeletes,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        RemovePendingDelete(pendingDeletes, operation.RelativePath);

        var identity = isDirectory ? null : await LoadTrackedIdentityAsync(operation.RelativePath, cancellationToken);
        pendingDeletes.Add(new PendingDeleteCandidate(
            operation.RelativePath,
            identity,
            isDirectory,
            DateTime.UtcNow.Add(_deleteGracePeriod),
            operation.Sequence));

        _logger.LogDebug(
            "Watcher staging {EntryType} delete for library {Library}: {Path} (identity: {Identity})",
            isDirectory ? "directory" : "file",
            _library.Name,
            operation.RelativePath,
            FormatIdentity(identity));
    }

    private void StagePendingDirectoryCreate(
        string relativePath,
        long sequence,
        List<PendingDirectoryCreateCandidate> pendingDirectoryCreates)
    {
        RemovePendingDirectoryCreate(pendingDirectoryCreates, relativePath);
        pendingDirectoryCreates.Add(new PendingDirectoryCreateCandidate(
            relativePath,
            DateTime.UtcNow.Add(_deleteGracePeriod),
            sequence));
        _logger.LogDebug(
            "Watcher staging directory create for library {Library}: {Path}",
            _library.Name,
            relativePath);
    }

    private async Task<bool> IsTrackedDirectoryPrefixAsync(string relativePath, CancellationToken cancellationToken)
    {
        var normalizedPrefix = RelativePathMatcher.NormalizePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
        {
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var paths = await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => pf.LibraryId == _library.Id)
            .Select(pf => pf.RelativePath)
            .ToListAsync(cancellationToken);

        return paths.Any(path => IsPathWithinPrefix(path, normalizedPrefix));
    }

    private async Task<FileIdentity?> LoadTrackedIdentityAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var identity = await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => pf.LibraryId == _library.Id && pf.RelativePath == relativePath)
            .Select(pf => new { pf.FileIdentityDevice, pf.FileIdentityValue })
            .FirstOrDefaultAsync(cancellationToken);

        if (identity == null || string.IsNullOrWhiteSpace(identity.FileIdentityDevice) || string.IsNullOrWhiteSpace(identity.FileIdentityValue))
        {
            return null;
        }

        return new FileIdentity(identity.FileIdentityDevice, identity.FileIdentityValue);
    }

    private async Task FlushExpiredDeletesAsync(List<PendingDeleteCandidate> pendingDeletes, CancellationToken cancellationToken)
    {
        if (pendingDeletes.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expired = pendingDeletes
            .Where(p => p.ExpiresAtUtc <= now)
            .OrderBy(p => p.Sequence)
            .ToList();

        foreach (var pendingDelete in expired)
        {
            pendingDeletes.Remove(pendingDelete);
            await ProcessDeleteAsync(pendingDelete, cancellationToken);
        }
    }

    private async Task FlushAllDeletesAsync(List<PendingDeleteCandidate> pendingDeletes, CancellationToken cancellationToken)
    {
        foreach (var pendingDelete in pendingDeletes.OrderBy(p => p.Sequence).ToList())
        {
            pendingDeletes.Remove(pendingDelete);
            await ProcessDeleteAsync(pendingDelete, cancellationToken);
        }
    }

    private async Task ProcessDeleteAsync(PendingDeleteCandidate pendingDelete, CancellationToken cancellationToken)
    {
        if (pendingDelete.IsDirectory)
        {
            _logger.LogInformation("Watcher invoking deleted-directory processing for library {Library}: {Path}", _library.Name, pendingDelete.RelativePath);
            await _librarySyncProcessor.ProcessDeletedDirectoryAsync(ToLibraryEntity(), pendingDelete.RelativePath, cancellationToken);
            return;
        }

        _logger.LogDebug("Watcher invoking deleted-file processing for library {Library}: {Path}", _library.Name, pendingDelete.RelativePath);
        await _librarySyncProcessor.ProcessDeletedFileAsync(ToLibraryEntity(), pendingDelete.RelativePath, cancellationToken);
    }

    private static PendingDeleteCandidate? TryMatchPendingDelete(List<PendingDeleteCandidate> pendingDeletes, FileIdentity? identity)
    {
        if (identity == null)
        {
            return null;
        }

        var match = pendingDeletes
            .Where(p => !p.IsDirectory)
            .Where(p => IdentityEquals(p.Identity, identity))
            .OrderBy(p => p.Sequence)
            .FirstOrDefault();

        if (match != null)
        {
            pendingDeletes.Remove(match);
        }

        return match;
    }

    private static PendingDeleteCandidate? TryMatchPendingDirectoryDelete(List<PendingDeleteCandidate> pendingDeletes, string newRelativePath)
    {
        var normalizedLeaf = GetLeafName(newRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedLeaf))
        {
            return null;
        }

        var matches = pendingDeletes
            .Where(p => p.IsDirectory)
            .Where(p => string.Equals(GetLeafName(p.RelativePath), normalizedLeaf, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Sequence)
            .Take(2)
            .ToList();

        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        pendingDeletes.Remove(match);
        return match;
    }

    private static PendingDirectoryCreateCandidate? TryMatchPendingDirectoryCreate(
        List<PendingDirectoryCreateCandidate> pendingDirectoryCreates,
        string oldRelativePath)
    {
        var normalizedLeaf = GetLeafName(oldRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedLeaf))
        {
            return null;
        }

        var matches = pendingDirectoryCreates
            .Where(p => string.Equals(GetLeafName(p.RelativePath), normalizedLeaf, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Sequence)
            .Take(2)
            .ToList();

        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        pendingDirectoryCreates.Remove(match);
        return match;
    }

    private static void RemovePendingDelete(List<PendingDeleteCandidate> pendingDeletes, string relativePath)
    {
        pendingDeletes.RemoveAll(p => string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemovePendingDirectoryCreate(List<PendingDirectoryCreateCandidate> pendingDirectoryCreates, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        pendingDirectoryCreates.RemoveAll(p => string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetLeafName(string relativePath)
    {
        var normalizedPath = RelativePathMatcher.NormalizePath(relativePath);
        return Path.GetFileName(normalizedPath);
    }

    private static bool IsPathWithinPrefix(string relativePath, string normalizedPrefix)
    {
        var normalizedPath = RelativePathMatcher.NormalizePath(relativePath);
        return normalizedPath.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentityEquals(FileIdentity? left, FileIdentity? right)
        => left != null
            && right != null
            && string.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static string FormatIdentity(FileIdentity? identity)
        => identity == null ? "missing" : $"{identity.Device}|{identity.Value}";

    private static bool HasPendingExpirations(
        List<PendingDeleteCandidate> pendingDeletes,
        List<PendingDirectoryCreateCandidate> pendingDirectoryCreates)
        => pendingDeletes.Count > 0 || pendingDirectoryCreates.Count > 0;

    private static TimeSpan GetNextFlushDelay(
        List<PendingDeleteCandidate> pendingDeletes,
        List<PendingDirectoryCreateCandidate> pendingDirectoryCreates)
    {
        var now = DateTime.UtcNow;
        var nextExpiry = DateTime.MaxValue;
        if (pendingDeletes.Count > 0)
        {
            nextExpiry = pendingDeletes.Min(p => p.ExpiresAtUtc);
        }

        if (pendingDirectoryCreates.Count > 0)
        {
            nextExpiry = DateTime.Compare(nextExpiry, pendingDirectoryCreates.Min(p => p.ExpiresAtUtc)) < 0
                ? nextExpiry
                : pendingDirectoryCreates.Min(p => p.ExpiresAtUtc);
        }

        var delay = nextExpiry - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static void FlushExpiredDirectoryCreates(List<PendingDirectoryCreateCandidate> pendingDirectoryCreates)
    {
        if (pendingDirectoryCreates.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        pendingDirectoryCreates.RemoveAll(p => p.ExpiresAtUtc <= now);
    }

    private async Task<MediaSourceItem?> CreateMediaSourceItemAsync(string relativePath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaterializeRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCreateMediaSourceItem(relativePath, out var item))
            {
                return item;
            }

            if (attempt < MaterializeRetryCount - 1)
            {
                await Task.Delay(MaterializeRetryDelay, cancellationToken);
            }
        }

        return null;
    }

    private bool TryCreateMediaSourceItem(string relativePath, out MediaSourceItem item)
    {
        item = default!;

        var fullPath = Path.Combine(_library.Path, relativePath);
        if (!LibraryWatchPathHelper.IsSupportedFile(fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            item = new MediaSourceItem
            {
                FullPath = fullPath,
                RelativePath = relativePath,
                SizeBytes = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Library ToLibraryEntity()
        => new()
        {
            Id = _library.Id,
            Name = _library.Name,
            Path = _library.Path,
        };
}
