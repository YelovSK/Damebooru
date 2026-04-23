using System.Threading.Channels;
using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
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
    private readonly ILibrarySyncProcessor _librarySyncProcessor;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly LibraryWatchTrackedStateReader _trackedStateReader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _deleteGracePeriod;

    public LibraryWatchSessionFactory(
        ILibrarySyncProcessor librarySyncProcessor,
        IFileIdentityResolver fileIdentityResolver,
        LibraryWatchTrackedStateReader trackedStateReader,
        ILoggerFactory loggerFactory,
        IOptions<DamebooruConfig> options)
    {
        _librarySyncProcessor = librarySyncProcessor;
        _fileIdentityResolver = fileIdentityResolver;
        _trackedStateReader = trackedStateReader;
        _loggerFactory = loggerFactory;
        _debounceDelay = TimeSpan.FromMilliseconds(Math.Max(100, options.Value.Scanner.WatcherDebounceMs));
        _deleteGracePeriod = TimeSpan.FromMilliseconds(Math.Max(options.Value.Scanner.WatcherDebounceMs * 2, 3000));
    }

    internal LibraryWatchSession Create(LibraryWatchTarget library)
        => new(
            library,
            _librarySyncProcessor,
            _fileIdentityResolver,
            _trackedStateReader,
            _loggerFactory.CreateLogger<LibraryWatchSession>(),
            _debounceDelay,
            _deleteGracePeriod);
}

internal sealed class LibraryWatchSession
{
    private static readonly TimeSpan MaterializeRetryDelay = TimeSpan.FromMilliseconds(250);
    private const int MaterializeRetryCount = 8;

    private sealed record PendingLibraryOperation(
        LibraryWatchEventKind Kind,
        string RelativePath,
        string? OldRelativePath,
        bool IsDirectory,
        long Sequence);

    private readonly LibraryWatchTarget _library;
    private readonly ILibrarySyncProcessor _librarySyncProcessor;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly LibraryWatchTrackedStateReader _trackedStateReader;
    private readonly ILogger<LibraryWatchSession> _logger;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _deleteGracePeriod;

    public LibraryWatchSession(
        LibraryWatchTarget library,
        ILibrarySyncProcessor librarySyncProcessor,
        IFileIdentityResolver fileIdentityResolver,
        LibraryWatchTrackedStateReader trackedStateReader,
        ILogger<LibraryWatchSession> logger,
        TimeSpan debounceDelay,
        TimeSpan deleteGracePeriod)
    {
        _library = library;
        _librarySyncProcessor = librarySyncProcessor;
        _fileIdentityResolver = fileIdentityResolver;
        _trackedStateReader = trackedStateReader;
        _logger = logger;
        _debounceDelay = debounceDelay;
        _deleteGracePeriod = deleteGracePeriod;
    }

    public async Task ProcessAsync(ChannelReader<LibraryWatchEvent> reader, CancellationToken cancellationToken)
    {
        var pendingState = new LibraryWatchPendingState(_deleteGracePeriod);

        while (!cancellationToken.IsCancellationRequested)
        {
            var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            var expiryTask = pendingState.HasPendingExpirations
                ? Task.Delay(pendingState.GetNextFlushDelay(), cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var completedTask = await Task.WhenAny(waitTask, expiryTask);
            if (completedTask == expiryTask)
            {
                await FlushDeletesAsync(pendingState.TakeExpiredDeleteEntries(), cancellationToken);
                pendingState.FlushExpiredDirectoryCreates();
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
                await FlushDeletesAsync(pendingState.TakeExpiredDeleteEntries(), cancellationToken);
                pendingState.FlushExpiredDirectoryCreates();

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

                await ProcessOperationAsync(operation, pendingState, cancellationToken);
            }

            await FlushDeletesAsync(pendingState.TakeExpiredDeleteEntries(), cancellationToken);
            pendingState.FlushExpiredDirectoryCreates();
        }

        await FlushDeletesAsync(pendingState.TakeAllDeletes(), cancellationToken);
        pendingState.ClearDirectoryCreates();
    }

    private async Task ProcessOperationAsync(
        PendingLibraryOperation operation,
        LibraryWatchPendingState pendingState,
        CancellationToken cancellationToken)
    {
        var libraryEntity = ToLibraryEntity();

        switch (operation.Kind)
        {
            case LibraryWatchEventKind.Upsert:
            {
                if (operation.IsDirectory)
                {
                    var inferredMove = pendingState.TryMatchDirectoryMoveFromCreate(operation.RelativePath);
                    if (inferredMove != null)
                    {
                        _logger.LogInformation(
                            "Watcher inferred directory move for library {Library}: {OldPath} -> {NewPath}",
                            _library.Name,
                            inferredMove.Value.OldPath,
                            inferredMove.Value.NewPath);
                        await _librarySyncProcessor.ProcessMovedDirectoryAsync(
                            libraryEntity,
                            inferredMove.Value.OldPath,
                            inferredMove.Value.NewPath,
                            cancellationToken);
                    }
                    else
                    {
                        pendingState.StageDirectoryCreate(operation.RelativePath, operation.Sequence);
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
                var oldPath = pendingState.TryMatchFileMove(operation.RelativePath, identity);
                if (oldPath != null)
                {
                    _logger.LogDebug(
                        "Watcher inferred move for library {Library} by identity: {OldPath} -> {NewPath}",
                        _library.Name,
                        oldPath,
                        operation.RelativePath);
                    await _librarySyncProcessor.ProcessMovedFileAsync(libraryEntity, oldPath, item, cancellationToken);
                    break;
                }

                _logger.LogDebug("Watcher invoking changed-file processing for library {Library}: {Path}", _library.Name, operation.RelativePath);
                await _librarySyncProcessor.ProcessChangedFileAsync(libraryEntity, item, cancellationToken);
                break;
            }
            case LibraryWatchEventKind.Delete:
                if (operation.IsDirectory || await _trackedStateReader.IsTrackedDirectoryPrefixAsync(_library.Id, operation.RelativePath, cancellationToken))
                {
                    var inferredMove = pendingState.TryMatchDirectoryMoveFromDelete(operation.RelativePath);
                    if (inferredMove != null)
                    {
                        _logger.LogInformation(
                            "Watcher inferred directory move for library {Library}: {OldPath} -> {NewPath}",
                            _library.Name,
                            inferredMove.Value.OldPath,
                            inferredMove.Value.NewPath);
                        await _librarySyncProcessor.ProcessMovedDirectoryAsync(
                            libraryEntity,
                            inferredMove.Value.OldPath,
                            inferredMove.Value.NewPath,
                            cancellationToken);
                        break;
                    }

                    await StagePendingDeleteAsync(operation, pendingState, isDirectory: true, cancellationToken);
                    break;
                }

                await StagePendingDeleteAsync(operation, pendingState, isDirectory: false, cancellationToken);
                break;
            case LibraryWatchEventKind.Move:
            {
                if (string.IsNullOrWhiteSpace(operation.OldRelativePath))
                {
                    return;
                }

                if (operation.IsDirectory)
                {
                    pendingState.RemovePendingDirectoryCreate(operation.OldRelativePath);
                    pendingState.RemovePendingDirectoryCreate(operation.RelativePath);
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

                pendingState.RemovePendingDelete(operation.OldRelativePath);
                pendingState.RemovePendingDelete(operation.RelativePath);
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
        LibraryWatchPendingState pendingState,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        var identity = isDirectory
            ? null
            : await _trackedStateReader.LoadTrackedIdentityAsync(_library.Id, operation.RelativePath, cancellationToken);
        pendingState.StageDelete(operation.RelativePath, identity, isDirectory, operation.Sequence);

        _logger.LogDebug(
            "Watcher staging {EntryType} delete for library {Library}: {Path} (identity: {Identity})",
            isDirectory ? "directory" : "file",
            _library.Name,
            operation.RelativePath,
            FormatIdentity(identity));
    }

    private async Task FlushDeletesAsync(
        IReadOnlyList<(string RelativePath, bool IsDirectory)> deletes,
        CancellationToken cancellationToken)
    {
        foreach (var delete in deletes)
        {
            await ProcessDeleteAsync(delete.RelativePath, delete.IsDirectory, cancellationToken);
        }
    }

    private async Task ProcessDeleteAsync(string relativePath, bool isDirectory, CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            _logger.LogInformation("Watcher invoking deleted-directory processing for library {Library}: {Path}", _library.Name, relativePath);
            await _librarySyncProcessor.ProcessDeletedDirectoryAsync(ToLibraryEntity(), relativePath, cancellationToken);
            return;
        }

        _logger.LogDebug("Watcher invoking deleted-file processing for library {Library}: {Path}", _library.Name, relativePath);
        await _librarySyncProcessor.ProcessDeletedFileAsync(ToLibraryEntity(), relativePath, cancellationToken);
    }

    private static string FormatIdentity(FileIdentity? identity)
        => identity == null ? "missing" : $"{identity.Device}|{identity.Value}";

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
