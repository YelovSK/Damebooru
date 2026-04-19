using System.Threading.Channels;
using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
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
        DateTime ExpiresAtUtc,
        long Sequence);

    private sealed record PendingLibraryOperation(
        LibraryWatchEventKind Kind,
        string RelativePath,
        string? OldRelativePath,
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

        while (!cancellationToken.IsCancellationRequested)
        {
            var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            var expiryTask = pendingDeletes.Count > 0
                ? Task.Delay(GetNextFlushDelay(pendingDeletes), cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var completedTask = await Task.WhenAny(waitTask, expiryTask);
            if (completedTask == expiryTask)
            {
                await FlushExpiredDeletesAsync(pendingDeletes, cancellationToken);
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
                _logger.LogDebug(
                    "Watcher compacted operation for library {Library}: {Kind} {OldPath} -> {Path}",
                    _library.Name,
                    operation.Kind,
                    operation.OldRelativePath ?? "-",
                    operation.RelativePath);
                await ProcessOperationAsync(operation, pendingDeletes, cancellationToken);
            }

            await FlushExpiredDeletesAsync(pendingDeletes, cancellationToken);
        }

        await FlushAllDeletesAsync(pendingDeletes, cancellationToken);
    }

    private async Task ProcessOperationAsync(
        PendingLibraryOperation operation,
        List<PendingDeleteCandidate> pendingDeletes,
        CancellationToken cancellationToken)
    {
        var libraryEntity = ToLibraryEntity();

        switch (operation.Kind)
        {
            case LibraryWatchEventKind.Upsert:
            {
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
                await StagePendingDeleteAsync(operation, pendingDeletes, cancellationToken);
                break;
            case LibraryWatchEventKind.Move:
            {
                if (string.IsNullOrWhiteSpace(operation.OldRelativePath))
                {
                    return;
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
                            watchEvent.Sequence);
                    }
                    else
                    {
                        map[watchEvent.RelativePath] = new PendingLibraryOperation(
                            LibraryWatchEventKind.Delete,
                            watchEvent.RelativePath,
                            null,
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
                                oldExisting.Sequence);
                            break;
                        }

                        if (oldExisting.Kind == LibraryWatchEventKind.Upsert)
                        {
                            map[watchEvent.RelativePath] = new PendingLibraryOperation(
                                LibraryWatchEventKind.Upsert,
                                watchEvent.RelativePath,
                                null,
                                oldExisting.Sequence);
                            break;
                        }
                    }

                    map.Remove(watchEvent.RelativePath);
                    map[watchEvent.RelativePath] = new PendingLibraryOperation(
                        LibraryWatchEventKind.Move,
                        watchEvent.RelativePath,
                        watchEvent.OldRelativePath,
                        watchEvent.Sequence);
                    break;

                case LibraryWatchEventKind.Overflow:
                    map[$"__overflow__{watchEvent.Sequence}"] = new PendingLibraryOperation(
                        LibraryWatchEventKind.Overflow,
                        string.Empty,
                        null,
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
        CancellationToken cancellationToken)
    {
        RemovePendingDelete(pendingDeletes, operation.RelativePath);

        var identity = await LoadTrackedIdentityAsync(operation.RelativePath, cancellationToken);
        pendingDeletes.Add(new PendingDeleteCandidate(
            operation.RelativePath,
            identity,
            DateTime.UtcNow.Add(_deleteGracePeriod),
            operation.Sequence));

        _logger.LogDebug(
            "Watcher staging delete for library {Library}: {Path} (identity: {Identity})",
            _library.Name,
            operation.RelativePath,
            FormatIdentity(identity));
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
            await ProcessDeleteAsync(pendingDelete.RelativePath, cancellationToken);
        }
    }

    private async Task FlushAllDeletesAsync(List<PendingDeleteCandidate> pendingDeletes, CancellationToken cancellationToken)
    {
        foreach (var pendingDelete in pendingDeletes.OrderBy(p => p.Sequence).ToList())
        {
            pendingDeletes.Remove(pendingDelete);
            await ProcessDeleteAsync(pendingDelete.RelativePath, cancellationToken);
        }
    }

    private async Task ProcessDeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Watcher invoking deleted-file processing for library {Library}: {Path}", _library.Name, relativePath);
        await _librarySyncProcessor.ProcessDeletedFileAsync(ToLibraryEntity(), relativePath, cancellationToken);
    }

    private static PendingDeleteCandidate? TryMatchPendingDelete(List<PendingDeleteCandidate> pendingDeletes, FileIdentity? identity)
    {
        if (identity == null)
        {
            return null;
        }

        var match = pendingDeletes
            .Where(p => IdentityEquals(p.Identity, identity))
            .OrderBy(p => p.Sequence)
            .FirstOrDefault();

        if (match != null)
        {
            pendingDeletes.Remove(match);
        }

        return match;
    }

    private static void RemovePendingDelete(List<PendingDeleteCandidate> pendingDeletes, string relativePath)
    {
        pendingDeletes.RemoveAll(p => string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IdentityEquals(FileIdentity? left, FileIdentity? right)
        => left != null
            && right != null
            && string.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static string FormatIdentity(FileIdentity? identity)
        => identity == null ? "missing" : $"{identity.Device}|{identity.Value}";

    private static TimeSpan GetNextFlushDelay(List<PendingDeleteCandidate> pendingDeletes)
    {
        var now = DateTime.UtcNow;
        var nextExpiry = pendingDeletes.Min(p => p.ExpiresAtUtc);
        var delay = nextExpiry - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
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
