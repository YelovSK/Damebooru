using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Services.Scanning;

public class LibrarySyncService : ILibrarySyncProcessor
{
    private const int OrphanDeleteBatchSize = 100;
    private const int FolderTagBatchSize = 500;

    private readonly ILogger<LibrarySyncService> _logger;
    private readonly IHasherService _hasher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaSource _mediaSource;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly FolderTaggingService _folderTaggingService;
    private readonly MediaEnrichmentService _mediaEnrichmentService;
    private readonly int _scanParallelism;

    public LibrarySyncService(
        ILogger<LibrarySyncService> logger,
        IHasherService hasher,
        IServiceScopeFactory scopeFactory,
        IMediaSource mediaSource,
        IFileIdentityResolver fileIdentityResolver,
        FolderTaggingService folderTaggingService,
        MediaEnrichmentService mediaEnrichmentService,
        IOptions<DamebooruConfig> options)
    {
        _logger = logger;
        _hasher = hasher;
        _scopeFactory = scopeFactory;
        _mediaSource = mediaSource;
        _fileIdentityResolver = fileIdentityResolver;
        _folderTaggingService = folderTaggingService;
        _mediaEnrichmentService = mediaEnrichmentService;
        _scanParallelism = Math.Max(1, options.Value.Scanner.Parallelism);
    }

    public async Task<ScanResult> ProcessDirectoryAsync(
        Library library,
        string directoryPath,
        IProgress<float>? progress = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        status?.Report($"Counting files in {directoryPath}...");
        var total = await _mediaSource.CountAsync(directoryPath, cancellationToken);
        _logger.LogInformation("Found {Count} files to process in library {Library}", total, library.Name);

        status?.Report("Loading existing posts database...");
        LibraryScanDiff diff;
        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
            diff = await LibraryScanDiff.LoadAsync(dbContext, library.Id, _hasher, _fileIdentityResolver, _logger, cancellationToken);
        }

        _logger.LogInformation("Loaded {Count} tracked files for library {Library}", diff.TrackedCount, library.Name);

        status?.Report("Scanning files...");
        var inspected = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _scanParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(_mediaSource.GetItemsAsync(directoryPath, cancellationToken), parallelOptions, async (item, ct) =>
        {
            await diff.InspectAsync(item, ct);

            var current = Interlocked.Increment(ref inspected);
            if (total > 0 && (current % 10 == 0 || current == total))
            {
                progress?.Report((float)current / total * 80);
                status?.Report($"Scanning: {current}/{total} files");
            }
        });

        var changes = diff.Build();
        _logger.LogInformation(
            "Applying changes to library {Library}: {ChangedCount} changed, {MovedCount} moved, {NewCount} new, {OrphanCount} removed",
            library.Name,
            changes.Updates.Count,
            changes.Moves.Count,
            changes.NewFiles.Count,
            changes.OrphanPostFileIds.Count);
        status?.Report($"Updating {changes.Updates.Count + changes.Moves.Count} files ({changes.Updates.Count} changed, {changes.Moves.Count} moved)...");

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

            foreach (var change in changes.Updates.Concat(changes.Moves))
            {
                var postFile = await dbContext.PostFiles.FindAsync([change.PostFileId], cancellationToken);
                if (postFile != null)
                {
                    await PostFileWriter.ApplyAsync(dbContext, postFile, change.Snapshot, cancellationToken);
                }
            }

            foreach (var move in changes.Moves)
            {
                _logger.LogInformation("Moved file: {OldPath} -> {NewPath}", move.OldRelativePath, move.Snapshot.RelativePath);
            }

            foreach (var snapshot in changes.NewFiles)
            {
                await PostFileWriter.AddAsync(dbContext, library.Id, snapshot, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            if (changes.OrphanPostFileIds.Count > 0)
            {
                status?.Report($"Removing {changes.OrphanPostFileIds.Count} orphaned files...");
                foreach (var batch in changes.OrphanPostFileIds.Chunk(OrphanDeleteBatchSize))
                {
                    await dbContext.PostFiles
                        .Where(pf => batch.Contains(pf.Id))
                        .ExecuteDeleteAsync(cancellationToken);
                }
            }

            var deletedPostCount = await PostFileWriter.DeleteEmptyPostsAsync(dbContext, cancellationToken);
            if (deletedPostCount > 0)
            {
                _logger.LogInformation("Deleted {Count} posts left without files", deletedPostCount);
            }
        }

        status?.Report($"Reconciling folder tags for {library.Name}...");
        await ReconcileLibraryFolderTagsAsync(library.Id, cancellationToken);

        var result = new ScanResult(
            changes.Scanned,
            changes.NewFiles.Count,
            changes.Updates.Count,
            changes.Moves.Count,
            changes.OrphanPostFileIds.Count);

        progress?.Report(100);
        status?.Report($"Finished scanning {library.Name} - {result.Scanned} files, {result.Added} added, {result.Updated} updated, {result.Moved} moved, {result.Removed} orphans removed");
        return result;
    }

    public async Task ProcessChangedFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var postFile = await FindPostFileAsync(dbContext, library.Id, item.RelativePath, cancellationToken);
        var snapshot = await ReadIndexableFileAsync(dbContext, library.Id, item, cancellationToken);

        if (snapshot == null)
        {
            if (postFile != null)
            {
                dbContext.PostFiles.Remove(postFile);
                await CommitAsync(dbContext, library, [postFile.PostId], [], [], cancellationToken);
            }

            return;
        }

        if (postFile == null)
        {
            var added = await PostFileWriter.AddAsync(dbContext, library.Id, snapshot, cancellationToken);
            await CommitAsync(dbContext, library, [], [added], [added], cancellationToken);
            return;
        }

        var previousPostId = postFile.PostId;
        var contentChanged = await PostFileWriter.ApplyAsync(dbContext, postFile, snapshot, cancellationToken);
        await CommitAsync(dbContext, library, [previousPostId], [postFile], contentChanged ? [postFile] : [], cancellationToken);
    }

    public async Task ProcessDeletedFileAsync(Library library, string relativePath, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var postFile = await FindPostFileAsync(dbContext, library.Id, relativePath, cancellationToken);
        if (postFile == null)
        {
            return;
        }

        dbContext.PostFiles.Remove(postFile);
        await CommitAsync(dbContext, library, [postFile.PostId], [], [], cancellationToken);
    }

    public async Task ProcessDeletedDirectoryAsync(Library library, string relativePathPrefix, CancellationToken cancellationToken)
    {
        var prefix = RelativePathMatcher.NormalizePath(relativePathPrefix);
        if (string.IsNullOrEmpty(prefix))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var postFiles = await QueryPostFilesUnderAsync(dbContext, library.Id, prefix, cancellationToken);
        if (postFiles.Count == 0)
        {
            return;
        }

        dbContext.PostFiles.RemoveRange(postFiles);
        await CommitAsync(dbContext, library, postFiles.Select(pf => pf.PostId), [], [], cancellationToken);
    }

    public async Task ProcessMovedFileAsync(Library library, string oldRelativePath, MediaSourceItem item, CancellationToken cancellationToken)
    {
        var oldPath = RelativePathMatcher.NormalizePath(oldRelativePath);
        var newPath = RelativePathMatcher.NormalizePath(item.RelativePath);
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            await ProcessChangedFileAsync(library, item, cancellationToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var source = await FindPostFileAsync(dbContext, library.Id, oldPath, cancellationToken);
        if (source == null)
        {
            _logger.LogWarning(
                "Move source path not found in library {Library}: {OldPath}; treating {NewPath} as changed",
                library.Name,
                oldPath,
                newPath);
            await ProcessChangedFileAsync(library, item, cancellationToken);
            return;
        }

        var previousPostIds = new List<int> { source.PostId };
        var snapshot = await ReadIndexableFileAsync(dbContext, library.Id, item, cancellationToken);
        if (snapshot == null)
        {
            dbContext.PostFiles.Remove(source);
            await CommitAsync(dbContext, library, previousPostIds, [], [], cancellationToken);
            return;
        }

        // The move overwrote whatever was tracked at the destination.
        var replaced = await FindPostFileAsync(dbContext, library.Id, newPath, cancellationToken);
        if (replaced != null)
        {
            previousPostIds.Add(replaced.PostId);
            dbContext.PostFiles.Remove(replaced);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var contentChanged = await PostFileWriter.ApplyAsync(dbContext, source, snapshot, cancellationToken);
        await CommitAsync(dbContext, library, previousPostIds, [source], contentChanged ? [source] : [], cancellationToken);
    }

    public async Task ProcessMovedDirectoryAsync(
        Library library,
        string oldRelativePathPrefix,
        string newRelativePathPrefix,
        CancellationToken cancellationToken)
    {
        var oldPrefix = RelativePathMatcher.NormalizePath(oldRelativePathPrefix);
        var newPrefix = RelativePathMatcher.NormalizePath(newRelativePathPrefix);
        if (string.IsNullOrEmpty(oldPrefix)
            || string.IsNullOrEmpty(newPrefix)
            || string.Equals(oldPrefix, newPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var postFiles = await QueryPostFilesUnderAsync(dbContext, library.Id, oldPrefix, cancellationToken);
        _logger.LogInformation(
            "Moving {Count} files in library {Library}: {OldPath} -> {NewPath}",
            postFiles.Count,
            library.Name,
            oldPrefix,
            newPrefix);
        if (postFiles.Count == 0)
        {
            return;
        }

        foreach (var postFile in postFiles)
        {
            PostFileWriter.SetPath(postFile, RelativePathMatcher.ReplacePrefix(postFile.RelativePath, oldPrefix, newPrefix));
        }

        await CommitAsync(dbContext, library, [], postFiles, [], cancellationToken);
    }

    /// <summary>
    /// Returns what should be indexed for the file, or null when it is ignored, excluded or unreadable.
    /// </summary>
    private async Task<FileSnapshot?> ReadIndexableFileAsync(
        DamebooruDbContext dbContext,
        int libraryId,
        MediaSourceItem item,
        CancellationToken cancellationToken)
    {
        var relativePath = RelativePathMatcher.NormalizePath(item.RelativePath);
        var rules = await LibraryScanRules.LoadAsync(dbContext, libraryId, cancellationToken);
        if (rules.IsIgnored(relativePath))
        {
            return null;
        }

        var hash = await _hasher.ComputeContentHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash) || rules.IsExcluded(relativePath, hash))
        {
            return null;
        }

        return new FileSnapshot(
            relativePath,
            hash,
            item.SizeBytes,
            item.LastModifiedUtc,
            _fileIdentityResolver.TryResolve(item.FullPath));
    }

    /// <summary>
    /// Saves pending file changes, then brings posts back in line: empty posts are deleted,
    /// folder tags follow the files' paths, and files with new content get their media data regenerated.
    /// </summary>
    private async Task CommitAsync(
        DamebooruDbContext dbContext,
        Library library,
        IEnumerable<int> previousPostIds,
        IReadOnlyCollection<PostFile> touchedFiles,
        IReadOnlyCollection<PostFile> filesToEnrich,
        CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        await PostFileWriter.DeleteEmptyPostsAsync(dbContext, cancellationToken);

        var affectedPostIds = previousPostIds
            .Concat(touchedFiles.Select(pf => pf.PostId))
            .Distinct()
            .ToList();
        var remainingPostIds = await dbContext.Posts
            .AsNoTracking()
            .Where(p => affectedPostIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        await _folderTaggingService.SyncPostFolderTagsAsync(dbContext, remainingPostIds, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var postFile in filesToEnrich)
        {
            await EnrichAsync(dbContext, library, postFile, cancellationToken);
        }
    }

    private async Task EnrichAsync(DamebooruDbContext dbContext, Library library, PostFile postFile, CancellationToken cancellationToken)
    {
        var target = new PostFileEnrichmentTarget(
            postFile.Id,
            postFile.LibraryId,
            postFile.ContentHash,
            postFile.RelativePath,
            library.Path);

        try
        {
            var metadata = await _mediaEnrichmentService.ExtractMetadataAsync(target, cancellationToken);
            postFile.Width = metadata.Width;
            postFile.Height = metadata.Height;
            postFile.ContentType = metadata.ContentType;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich metadata for post file {Id}: {Path}", postFile.Id, postFile.RelativePath);
        }

        try
        {
            var similarity = await _mediaEnrichmentService.ComputeSimilarityAsync(target, cancellationToken);
            postFile.PdqHash256 = similarity?.PdqHash256;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich similarity for post file {Id}: {Path}", postFile.Id, postFile.RelativePath);
        }

        try
        {
            await _mediaEnrichmentService.GenerateGeneratedImagesAsync(target, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich thumbnail/preview for post file {Id}: {Path}", postFile.Id, postFile.RelativePath);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileLibraryFolderTagsAsync(int libraryId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var lastPostId = 0;
        while (true)
        {
            var postIds = await dbContext.Posts
                .AsNoTracking()
                .Where(p => p.Id > lastPostId)
                .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId))
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(FolderTagBatchSize)
                .ToListAsync(cancellationToken);

            if (postIds.Count == 0)
            {
                break;
            }

            lastPostId = postIds[^1];
            await _folderTaggingService.SyncPostFolderTagsAsync(dbContext, postIds, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static Task<PostFile?> FindPostFileAsync(
        DamebooruDbContext dbContext,
        int libraryId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = RelativePathMatcher.NormalizePath(relativePath);
        return dbContext.PostFiles.FirstOrDefaultAsync(
            pf => pf.LibraryId == libraryId && pf.RelativePath == normalizedPath,
            cancellationToken);
    }

    private static Task<List<PostFile>> QueryPostFilesUnderAsync(
        DamebooruDbContext dbContext,
        int libraryId,
        string normalizedPrefix,
        CancellationToken cancellationToken)
    {
        var prefixWithSlash = normalizedPrefix + "/";
        return dbContext.PostFiles
            .Where(pf => pf.LibraryId == libraryId)
            .Where(pf => pf.RelativePath == normalizedPrefix || pf.RelativePath.StartsWith(prefixWithSlash))
            .ToListAsync(cancellationToken);
    }
}
