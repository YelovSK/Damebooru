using Damebooru.Core;
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
using System.Collections.Concurrent;

namespace Damebooru.Processing.Services.Scanning;

public class LibrarySyncService : ILibrarySyncProcessor
{
    private sealed record ExistingPostFileInfo(
        int PostId,
        int PostFileId,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime FileModifiedDate,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed record NewFileCandidate(
        string FullPath,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime LastModifiedUtc,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed record PotentialMoveCandidate(
        string FullPath,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime LastModifiedUtc,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed record MoveUpdate(
        int PostId,
        int PostFileId,
        string OldRelativePath,
        string NewRelativePath,
        string Hash,
        long NewSize,
        DateTime NewMtime,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed record PostFileUpdateCandidate(
        int PostId,
        int PostFileId,
        string NewHash,
        long NewSize,
        DateTime NewMtime,
        bool HashChanged,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed class SyncRunState
    {
        public required Dictionary<string, ExistingPostFileInfo> ExistingFilesByPath { get; init; }
        public required Dictionary<string, List<ExistingPostFileInfo>> ExistingFilesByIdentity { get; init; }
        public required Dictionary<string, string> ExcludedHashesByPath { get; init; }
        public required HashSet<string> IgnoredPathPrefixes { get; init; }

        public ConcurrentDictionary<string, byte> SeenPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<PostFileUpdateCandidate> PostFilesToUpdate { get; } = [];
        public ConcurrentBag<PotentialMoveCandidate> PotentialMoves { get; } = [];
        public ConcurrentBag<NewFileCandidate> NewFiles { get; } = [];
    }

    private sealed record MoveResolution(List<MoveUpdate> Moves, List<PotentialMoveCandidate> UnmatchedCandidates);

    private readonly ILogger<LibrarySyncService> _logger;
    private readonly IHasherService _hasher;
    private readonly IPostIngestionService _ingestionService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaSource _mediaSource;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly FolderTaggingService _folderTaggingService;
    private readonly int _scanParallelism;

    public LibrarySyncService(
        ILogger<LibrarySyncService> logger,
        IHasherService hasher,
        IPostIngestionService ingestionService,
        IServiceScopeFactory scopeFactory,
        IMediaSource mediaSource,
        IFileIdentityResolver fileIdentityResolver,
        FolderTaggingService folderTaggingService,
        IOptions<DamebooruConfig> options)
    {
        _logger = logger;
        _hasher = hasher;
        _ingestionService = ingestionService;
        _scopeFactory = scopeFactory;
        _mediaSource = mediaSource;
        _fileIdentityResolver = fileIdentityResolver;
        _folderTaggingService = folderTaggingService;
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
        _logger.LogInformation("Counting files in {Path}...", directoryPath);
        var total = await _mediaSource.CountAsync(directoryPath, cancellationToken);
        _logger.LogInformation("Found {Count} files to process in library {Library}", total, library.Name);

        status?.Report("Loading existing posts database...");
        _logger.LogInformation("Loading existing posts for library {Library}...", library.Name);

        var state = await LoadStateAsync(library.Id, cancellationToken);
        _logger.LogInformation(
            "Loaded {Count} existing files and {IdentityCount} identity buckets.",
            state.ExistingFilesByPath.Count,
            state.ExistingFilesByIdentity.Count);

        status?.Report("Scanning files...");
        _logger.LogInformation("Streaming files from {Path}...", directoryPath);

        var scanned = 0;
        var addedCount = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _scanParallelism,
            CancellationToken = cancellationToken
        };

        var items = _mediaSource.GetItemsAsync(directoryPath, cancellationToken);
        await Parallel.ForEachAsync(items, parallelOptions, async (item, ct) =>
        {
            await ProcessFileOptimizedAsync(library, item, state, ct);

            Interlocked.Increment(ref scanned);
            if (scanned % 10 == 0 || scanned == total)
            {
                if (total > 0)
                {
                    progress?.Report((float)scanned / total * 80);
                    status?.Report($"Scanning: {scanned}/{total} files");
                }
            }
        });

        var moveResolution = ResolveMoveCandidates(state);
        var movedPosts = moveResolution.Moves;

        if (!state.PostFilesToUpdate.IsEmpty || movedPosts.Count > 0 || !state.NewFiles.IsEmpty || moveResolution.UnmatchedCandidates.Count > 0)
        {
            var totalUpdates = state.PostFilesToUpdate.Count + movedPosts.Count;
            status?.Report($"Updating {totalUpdates} files ({state.PostFilesToUpdate.Count} changed, {movedPosts.Count} moved)...");

            _logger.LogInformation(
                "Updating files in library {Library}: {ChangedCount} changed, {MovedCount} moved, {NewCount} new",
                library.Name,
                state.PostFilesToUpdate.Count,
                movedPosts.Count,
                state.NewFiles.Count + moveResolution.UnmatchedCandidates.Count);

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

            foreach (var update in state.PostFilesToUpdate)
            {
                await ApplyPostFileUpdateAsync(dbContext, library, update, cancellationToken);
            }

            foreach (var move in movedPosts)
            {
                await ApplyMoveAsync(dbContext, move, cancellationToken);

                _logger.LogInformation(
                    "Moved post {PostId}: {OldPath} -> {NewPath}",
                    move.PostId,
                    move.OldRelativePath,
                    move.NewRelativePath);
            }

            foreach (var candidate in state.NewFiles)
            {
                await PersistNewFileAsync(dbContext, library, candidate, cancellationToken);
                addedCount++;
            }

            foreach (var candidate in moveResolution.UnmatchedCandidates)
            {
                await PersistNewFileAsync(dbContext, library, new NewFileCandidate(
                    candidate.FullPath,
                    candidate.RelativePath,
                    candidate.Hash,
                    candidate.SizeBytes,
                    candidate.LastModifiedUtc,
                    candidate.FileIdentityDevice,
                    candidate.FileIdentityValue), cancellationToken);
                addedCount++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var orphanPaths = state.ExistingFilesByPath.Keys
            .Where(path => !state.SeenPaths.ContainsKey(path))
            .ToList();

        if (orphanPaths.Count > 0)
        {
            status?.Report($"Removing {orphanPaths.Count} orphaned posts...");
            _logger.LogInformation("Removing {Count} orphaned posts from library {Library}", orphanPaths.Count, library.Name);

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

            const int batchSize = 100;
            for (var i = 0; i < orphanPaths.Count; i += batchSize)
            {
                var batch = orphanPaths.Skip(i).Take(batchSize).ToList();
                var orphanFileIds = batch.Select(path => state.ExistingFilesByPath[path].PostFileId).ToList();

                await dbContext.PostFiles
                    .Where(pf => orphanFileIds.Contains(pf.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Removed {Count} orphaned files", orphanPaths.Count);
        }

        status?.Report($"Reconciling folder tags for {library.Name}...");
        await ReconcileLibraryFolderTagsAsync(library.Id, cancellationToken);

        progress?.Report(100);
        status?.Report($"Finished scanning {library.Name} - {scanned} files, {addedCount} added, {state.PostFilesToUpdate.Count} updated, {movedPosts.Count} moved, {orphanPaths.Count} orphans removed");

        return new ScanResult(scanned, addedCount, state.PostFilesToUpdate.Count, movedPosts.Count, orphanPaths.Count);
    }

    public async Task ProcessFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var existingPostFile = await dbContext.PostFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pf => pf.LibraryId == library.Id && pf.RelativePath == item.RelativePath, cancellationToken);

        if (existingPostFile != null)
        {
            return;
        }

        var hash = await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        var post = await PersistNewFileAsync(dbContext, library, new NewFileCandidate(
            item.FullPath,
            item.RelativePath,
            hash,
            item.SizeBytes,
            item.LastModifiedUtc,
            identity?.Device,
            identity?.Value), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (post.Id > 0)
        {
            await _folderTaggingService.SyncPostFolderTagsAsync(dbContext, [post.Id], cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<SyncRunState> LoadStateAsync(int libraryId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var libraryPostFiles = await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => pf.LibraryId == libraryId)
            .Select(pf => new
            {
                pf.PostId,
                pf.Id,
                pf.RelativePath,
                pf.ContentHash,
                pf.SizeBytes,
                pf.FileModifiedDate,
                pf.FileIdentityDevice,
                pf.FileIdentityValue
            })
            .ToListAsync(cancellationToken);

        var existingPostFiles = libraryPostFiles
            .GroupBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var first = g.First();
                    return new ExistingPostFileInfo(
                        first.PostId,
                        first.Id,
                        first.RelativePath,
                        first.ContentHash,
                        first.SizeBytes,
                        first.FileModifiedDate,
                        first.FileIdentityDevice,
                        first.FileIdentityValue);
                },
                StringComparer.OrdinalIgnoreCase);

        var existingPostFilesByIdentity = libraryPostFiles
            .Select(p => new
            {
                PostFile = new ExistingPostFileInfo(
                    p.PostId,
                    p.Id,
                    p.RelativePath,
                    p.ContentHash,
                    p.SizeBytes,
                    p.FileModifiedDate,
                    p.FileIdentityDevice,
                    p.FileIdentityValue),
                Key = BuildIdentityKey(p.FileIdentityDevice, p.FileIdentityValue)
            })
            .Where(x => x.Key != null)
            .GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.PostFile).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var excludedHashesByPath = (await dbContext.ExcludedFiles
            .AsNoTracking()
            .Where(e => e.LibraryId == libraryId)
            .Select(e => new { e.RelativePath, e.ContentHash })
            .ToListAsync(cancellationToken))
            .Where(e => !string.IsNullOrWhiteSpace(e.ContentHash))
            .ToDictionary(e => e.RelativePath, e => e.ContentHash, StringComparer.OrdinalIgnoreCase);

        var ignoredPathPrefixes = (await dbContext.LibraryIgnoredPaths
            .AsNoTracking()
            .Where(p => p.LibraryId == libraryId)
            .Select(p => p.RelativePathPrefix)
            .ToListAsync(cancellationToken))
            .Select(RelativePathMatcher.NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SyncRunState
        {
            ExistingFilesByPath = existingPostFiles,
            ExistingFilesByIdentity = existingPostFilesByIdentity,
            ExcludedHashesByPath = excludedHashesByPath,
            IgnoredPathPrefixes = ignoredPathPrefixes
        };
    }

    private async Task ProcessFileOptimizedAsync(Library library, MediaSourceItem item, SyncRunState state, CancellationToken cancellationToken)
    {
        var relativePath = item.RelativePath;
        var normalizedRelativePath = RelativePathMatcher.NormalizePath(relativePath);
        if (state.IgnoredPathPrefixes.Any(prefix => RelativePathMatcher.IsWithinPrefix(normalizedRelativePath, prefix)))
        {
            return;
        }

        state.SeenPaths.TryAdd(relativePath, 0);

        string? precomputedHash = null;
        if (state.ExcludedHashesByPath.TryGetValue(relativePath, out var excludedHash))
        {
            var currentHash = await ComputeHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(currentHash))
            {
                return;
            }

            precomputedHash = currentHash;
            if (string.Equals(currentHash, excludedHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _logger.LogInformation("Exclusion mismatch for {Path}: path matched but hash changed, allowing ingest", relativePath);
        }

        if (state.ExistingFilesByPath.TryGetValue(relativePath, out var existing))
        {
            var fileChanged = item.SizeBytes != existing.SizeBytes
                || Math.Abs((item.LastModifiedUtc - existing.FileModifiedDate).TotalSeconds) > 1;
            var missingIdentity = string.IsNullOrWhiteSpace(existing.FileIdentityDevice)
                || string.IsNullOrWhiteSpace(existing.FileIdentityValue);

            if (!fileChanged)
            {
                if (!missingIdentity)
                {
                    return;
                }

                var resolvedIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
                var newDevice = resolvedIdentity?.Device ?? existing.FileIdentityDevice;
                var newValue = resolvedIdentity?.Value ?? existing.FileIdentityValue;

                var identityChanged = !string.Equals(existing.FileIdentityDevice, newDevice, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.FileIdentityValue, newValue, StringComparison.OrdinalIgnoreCase);

                if (!identityChanged)
                {
                    return;
                }

                state.PostFilesToUpdate.Add(new PostFileUpdateCandidate(
                    existing.PostId,
                    existing.PostFileId,
                    existing.Hash,
                    existing.SizeBytes,
                    existing.FileModifiedDate,
                    false,
                    newDevice,
                    newValue));

                return;
            }

            var changedIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
            var changedDevice = changedIdentity?.Device ?? existing.FileIdentityDevice;
            var changedValue = changedIdentity?.Value ?? existing.FileIdentityValue;

            var newHash = precomputedHash ?? await ComputeHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(newHash))
            {
                return;
            }

            var hashChanged = !string.Equals(newHash, existing.Hash, StringComparison.OrdinalIgnoreCase);
            state.PostFilesToUpdate.Add(new PostFileUpdateCandidate(
                existing.PostId,
                existing.PostFileId,
                newHash,
                item.SizeBytes,
                item.LastModifiedUtc,
                hashChanged,
                changedDevice,
                changedValue));

            if (hashChanged)
            {
                _logger.LogInformation("File changed: {Path} (size: {OldSize}->{NewSize})", relativePath, existing.SizeBytes, item.SizeBytes);
            }

            return;
        }

        var hash = precomputedHash ?? await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        var identityKey = BuildIdentityKey(identity?.Device, identity?.Value);
        if (identityKey != null
            && state.ExistingFilesByIdentity.TryGetValue(identityKey, out var candidatesByIdentity)
            && candidatesByIdentity.Count > 0)
        {
            state.PotentialMoves.Add(new PotentialMoveCandidate(
                item.FullPath,
                relativePath,
                hash,
                item.SizeBytes,
                item.LastModifiedUtc,
                identity?.Device,
                identity?.Value));
            return;
        }

        state.NewFiles.Add(new NewFileCandidate(
            item.FullPath,
            relativePath,
            hash,
            item.SizeBytes,
            item.LastModifiedUtc,
            identity?.Device,
            identity?.Value));
    }

    private MoveResolution ResolveMoveCandidates(SyncRunState state)
    {
        var moves = new List<MoveUpdate>();
        var unmatched = new List<PotentialMoveCandidate>();
        var movedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedPostFileIds = new HashSet<int>();

        foreach (var candidate in state.PotentialMoves)
        {
            var identityKey = BuildIdentityKey(candidate.FileIdentityDevice, candidate.FileIdentityValue);
            if (identityKey == null
                || !state.ExistingFilesByIdentity.TryGetValue(identityKey, out var candidatesByIdentity)
                || candidatesByIdentity.Count == 0)
            {
                unmatched.Add(candidate);
                continue;
            }

            var source = candidatesByIdentity.FirstOrDefault(existing =>
                !state.SeenPaths.ContainsKey(existing.RelativePath)
                && !movedOldPaths.Contains(existing.RelativePath)
                && !movedPostFileIds.Contains(existing.PostFileId));

            if (source == null)
            {
                unmatched.Add(candidate);
                continue;
            }

            moves.Add(new MoveUpdate(
                source.PostId,
                source.PostFileId,
                source.RelativePath,
                candidate.RelativePath,
                candidate.Hash,
                candidate.SizeBytes,
                candidate.LastModifiedUtc,
                candidate.FileIdentityDevice,
                candidate.FileIdentityValue));

            movedOldPaths.Add(source.RelativePath);
            movedPostFileIds.Add(source.PostFileId);
            state.SeenPaths.TryAdd(source.RelativePath, 0);
        }

        return new MoveResolution(moves, unmatched);
    }

    private async Task<int> CopyNonFolderTagsFromHashMatchesAsync(int libraryId, IReadOnlyCollection<string> newRelativePaths, CancellationToken cancellationToken)
    {
        if (newRelativePaths.Count == 0)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var newPosts = await dbContext.Posts
            .AsNoTracking()
            .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId && newRelativePaths.Contains(pf.RelativePath)))
            .Select(p => new
            {
                p.Id,
                ContentHash = p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.ContentHash).FirstOrDefault() ?? string.Empty
            })
            .ToListAsync(cancellationToken);

        if (newPosts.Count == 0)
        {
            return 0;
        }

        var hashSet = newPosts
            .Select(p => p.ContentHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (hashSet.Count == 0)
        {
            return 0;
        }

        var newPostIds = newPosts.Select(p => p.Id).ToHashSet();

        var donorAssignments = await dbContext.PostTags
            .AsNoTracking()
            .Where(pt => pt.Source != PostTagSource.Folder
                && pt.Post.PostFiles.Any(pf => pf.LibraryId == libraryId)
                && hashSet.Contains(pt.Post.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.ContentHash).FirstOrDefault() ?? string.Empty))
            .Select(pt => new
            {
                pt.PostId,
                ContentHash = pt.Post.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.ContentHash).FirstOrDefault() ?? string.Empty,
                pt.TagId,
                pt.Source
            })
            .ToListAsync(cancellationToken);

        if (donorAssignments.Count == 0)
        {
            return 0;
        }

        var donorByHash = donorAssignments
            .GroupBy(x => x.ContentHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var existingNewAssignments = await dbContext.PostTags
            .AsNoTracking()
            .Where(pt => newPostIds.Contains(pt.PostId))
            .Select(pt => new { pt.PostId, pt.TagId, pt.Source })
            .ToListAsync(cancellationToken);

        var existingSet = existingNewAssignments
            .Select(x => (x.PostId, x.TagId, x.Source))
            .ToHashSet();

        var inserted = 0;
        foreach (var newPost in newPosts)
        {
            if (!donorByHash.TryGetValue(newPost.ContentHash, out var donorRows))
            {
                continue;
            }

            var union = donorRows
                .Where(row => row.PostId != newPost.Id)
                .Select(row => (newPost.Id, row.TagId, row.Source))
                .Distinct();

            foreach (var row in union)
            {
                if (!existingSet.Add(row))
                {
                    continue;
                }

                dbContext.PostTags.Add(new PostTag
                {
                    PostId = row.Id,
                    TagId = row.TagId,
                    Source = row.Source
                });
                inserted++;
            }
        }

        if (inserted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return inserted;
    }

    private async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        return await _hasher.ComputeContentHashAsync(filePath, cancellationToken);
    }

    private async Task<List<int>> ApplyPostFileUpdateAsync(
        DamebooruDbContext dbContext,
        Library library,
        PostFileUpdateCandidate update,
        CancellationToken cancellationToken)
    {
        var postFile = await dbContext.PostFiles
            .Include(pf => pf.Post)
                .ThenInclude(p => p.PostFiles)
            .FirstOrDefaultAsync(pf => pf.Id == update.PostFileId, cancellationToken);

        if (postFile == null)
        {
            return [];
        }

        var oldPostId = postFile.PostId;
        var affectedPostIds = new HashSet<int> { oldPostId };
        var newContentType = SupportedMedia.GetMimeType(Path.GetExtension(postFile.RelativePath));

        if (update.HashChanged)
        {
            var canonicalPost = await FindCanonicalPostByHashAsync(dbContext, update.NewHash, cancellationToken);
            if (canonicalPost != null && canonicalPost.Id != postFile.PostId)
            {
                postFile.PostId = canonicalPost.Id;
                affectedPostIds.Add(canonicalPost.Id);
            }
            else if (canonicalPost == null)
            {
                var replacementPost = CreatePostEntity(
                    libraryId: library.Id,
                    relativePath: postFile.RelativePath,
                    hash: update.NewHash,
                    sizeBytes: update.NewSize,
                    fileModifiedDate: update.NewMtime,
                    fileIdentityDevice: update.FileIdentityDevice,
                    fileIdentityValue: update.FileIdentityValue,
                    contentType: newContentType,
                    importDateUtc: DateTime.UtcNow);

                dbContext.Posts.Add(replacementPost);
                postFile.Post = replacementPost;
            }

            postFile.Width = 0;
            postFile.Height = 0;
            postFile.PdqHash256 = null;
        }

        postFile.ContentHash = update.NewHash;
        postFile.SizeBytes = update.NewSize;
        postFile.FileModifiedDate = update.NewMtime;
        postFile.FileIdentityDevice = update.FileIdentityDevice;
        postFile.FileIdentityValue = update.FileIdentityValue;
        postFile.ContentType = newContentType;

        if (postFile.PostId != 0)
        {
            affectedPostIds.Add(postFile.PostId);
        }

        return affectedPostIds.ToList();
    }

    private async Task<List<int>> ApplyMoveAsync(
        DamebooruDbContext dbContext,
        MoveUpdate move,
        CancellationToken cancellationToken)
    {
        var postFile = await dbContext.PostFiles
            .FirstOrDefaultAsync(pf => pf.Id == move.PostFileId, cancellationToken);

        if (postFile == null)
        {
            return [];
        }

        postFile.RelativePath = move.NewRelativePath;
        postFile.ContentHash = move.Hash;
        postFile.SizeBytes = move.NewSize;
        postFile.FileModifiedDate = move.NewMtime;
        postFile.ContentType = SupportedMedia.GetMimeType(Path.GetExtension(move.NewRelativePath));
        postFile.FileIdentityDevice = move.FileIdentityDevice;
        postFile.FileIdentityValue = move.FileIdentityValue;

        return [move.PostId];
    }

    private async Task<Post> PersistNewFileAsync(
        DamebooruDbContext dbContext,
        Library library,
        NewFileCandidate candidate,
        CancellationToken cancellationToken)
    {
        var canonicalPost = await FindCanonicalPostByHashAsync(dbContext, candidate.Hash, cancellationToken);
        var contentType = SupportedMedia.GetMimeType(Path.GetExtension(candidate.RelativePath));

        if (canonicalPost == null)
        {
            var post = CreatePostEntity(
                library.Id,
                candidate.RelativePath,
                candidate.Hash,
                candidate.SizeBytes,
                candidate.LastModifiedUtc,
                candidate.FileIdentityDevice,
                candidate.FileIdentityValue,
                contentType,
                DateTime.UtcNow);

            post.PostFiles.Add(new PostFile
            {
                LibraryId = library.Id,
                RelativePath = candidate.RelativePath,
                ContentHash = candidate.Hash,
                SizeBytes = candidate.SizeBytes,
                FileModifiedDate = candidate.LastModifiedUtc,
                FileIdentityDevice = candidate.FileIdentityDevice,
                FileIdentityValue = candidate.FileIdentityValue,
                ContentType = contentType,
            });

            dbContext.Posts.Add(post);
            return post;
        }

        var newPostFile = new PostFile
        {
            LibraryId = library.Id,
            RelativePath = candidate.RelativePath,
            ContentHash = candidate.Hash,
            SizeBytes = candidate.SizeBytes,
            FileModifiedDate = candidate.LastModifiedUtc,
            FileIdentityDevice = candidate.FileIdentityDevice,
            FileIdentityValue = candidate.FileIdentityValue,
            ContentType = contentType,
        };

        // Newly created canonical posts can still be tracked with Id = 0 until SaveChanges.
        // Attach through the navigation in that case so EF inserts the parent first.
        if (canonicalPost.Id == 0)
        {
            canonicalPost.PostFiles.Add(newPostFile);
        }
        else
        {
            newPostFile.PostId = canonicalPost.Id;
            dbContext.PostFiles.Add(newPostFile);
        }

        return canonicalPost;
    }

    private async Task<Post?> FindCanonicalPostByHashAsync(
        DamebooruDbContext dbContext,
        string hash,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.Posts.Local
            .Where(p => p.PostFiles.Any(pf => string.Equals(pf.ContentHash, hash, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.ImportDate)
            .ThenBy(p => p.Id)
            .FirstOrDefault();

        if (tracked != null)
        {
            return tracked;
        }

        var postId = await dbContext.Posts
            .Where(p => p.PostFiles.Any(pf => pf.ContentHash == hash))
            .OrderBy(p => p.ImportDate)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (postId == 0)
        {
            return null;
        }

        return await dbContext.Posts
            .Include(p => p.PostFiles)
            .FirstAsync(p => p.Id == postId, cancellationToken);
    }

    private async Task RefreshLegacyPostRecordsAsync(
        DamebooruDbContext dbContext,
        IReadOnlyCollection<int> postIds,
        CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return;
        }

        var posts = await dbContext.Posts
            .Include(p => p.PostFiles)
            .Where(p => postIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var post in posts)
        {
            var representativeFile = post.PostFiles
                .OrderBy(pf => pf.Id)
                .FirstOrDefault();

            if (representativeFile == null)
            {
                dbContext.Posts.Remove(post);
            }
        }
    }

    private async Task ReconcileLibraryFolderTagsAsync(int libraryId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        const int batchSize = 500;
        var lastPostId = 0;

        while (true)
        {
            var postIds = await dbContext.Posts
                .AsNoTracking()
                .Where(p => p.Id > lastPostId)
                .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId))
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(batchSize)
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

    private static Post CreatePostEntity(
        int libraryId,
        string relativePath,
        string hash,
        long sizeBytes,
        DateTime fileModifiedDate,
        string? fileIdentityDevice,
        string? fileIdentityValue,
        string contentType,
        DateTime importDateUtc)
    {
        return new Post
        {
            ImportDate = importDateUtc,
        };
    }

    private static string? BuildIdentityKey(string? device, string? value)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return $"{device.Trim()}|{value.Trim()}";
    }
}
