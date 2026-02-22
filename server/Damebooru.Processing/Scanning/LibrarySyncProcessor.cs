using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Damebooru.Processing.Pipeline;

public class LibrarySyncProcessor : ILibrarySyncProcessor
{
    private readonly ILogger<LibrarySyncProcessor> _logger;
    private readonly IHasherService _hasher;
    private readonly IPostIngestionService _ingestionService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaSource _mediaSource;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly int _scanParallelism;

    public LibrarySyncProcessor(
        ILogger<LibrarySyncProcessor> logger,
        IHasherService hasher,
        IPostIngestionService ingestionService,
        IServiceScopeFactory scopeFactory,
        IMediaSource mediaSource,
        IFileIdentityResolver fileIdentityResolver,
        IOptions<DamebooruConfig> options)
    {
        _logger = logger;
        _hasher = hasher;
        _ingestionService = ingestionService;
        _scopeFactory = scopeFactory;
        _mediaSource = mediaSource;
        _fileIdentityResolver = fileIdentityResolver;
        _scanParallelism = Math.Max(1, options.Value.Scanner.Parallelism);
    }

    /// <summary>Snapshot of an existing post for fast in-memory comparison.</summary>
    private record ExistingPostInfo(
        int Id,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime FileModifiedDate,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    /// <summary>Potential file move/rename detected during scan via stable filesystem identity.</summary>
    private record PotentialMoveCandidate(
        string FullPath,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime LastModifiedUtc,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    /// <summary>Resolved post move/rename that should update one existing post path.</summary>
    private record MoveUpdate(
        int Id,
        string OldRelativePath,
        string NewRelativePath,
        string Hash,
        long NewSize,
        DateTime NewMtime,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    public async Task<ScanResult> ProcessDirectoryAsync(Library library, string directoryPath, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        status?.Report($"Counting files in {directoryPath}...");
        _logger.LogInformation("Counting files in {Path}...", directoryPath);
        var total = await _mediaSource.CountAsync(directoryPath, cancellationToken);
        _logger.LogInformation("Found {Count} files to process in library {Library}", total, library.Name);

        // Pre-load existing posts for comparison
        status?.Report($"Loading existing posts database...");
        _logger.LogInformation("Loading existing posts for library {Library}...", library.Name);

        Dictionary<string, ExistingPostInfo> existingPosts;
        Dictionary<string, List<ExistingPostInfo>> existingPostsByIdentity;
        HashSet<string> excludedPaths;
        HashSet<string> ignoredPathPrefixes;

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
            var libraryPosts = await dbContext.Posts
                .AsNoTracking()
                .Where(p => p.LibraryId == library.Id)
                .Select(p => new
                {
                    p.Id,
                    p.RelativePath,
                    p.ContentHash,
                    p.SizeBytes,
                    p.FileModifiedDate,
                    p.FileIdentityDevice,
                    p.FileIdentityValue
                })
                .ToListAsync(cancellationToken);

            existingPosts = libraryPosts
                .GroupBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var first = g.First();
                        return new ExistingPostInfo(
                            first.Id,
                            first.RelativePath,
                            first.ContentHash,
                            first.SizeBytes,
                            first.FileModifiedDate,
                            first.FileIdentityDevice,
                            first.FileIdentityValue);
                    },
                    StringComparer.OrdinalIgnoreCase);

            existingPostsByIdentity = libraryPosts
                .Select(p => new
                {
                    Post = new ExistingPostInfo(
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
                    g => g.Select(x => x.Post).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            excludedPaths = (await dbContext.ExcludedFiles
                .AsNoTracking()
                .Where(e => e.LibraryId == library.Id)
                .Select(e => e.RelativePath)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            ignoredPathPrefixes = (await dbContext.LibraryIgnoredPaths
                .AsNoTracking()
                .Where(p => p.LibraryId == library.Id)
                .Select(p => p.RelativePathPrefix)
                .ToListAsync(cancellationToken))
                .Select(RelativePathMatcher.NormalizePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        _logger.LogInformation(
            "Loaded {Count} existing posts and {IdentityCount} identity buckets.",
            existingPosts.Count,
            existingPostsByIdentity.Count);


        // Track which paths we see on disk for orphan detection
        var seenPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Track posts that need updating (changed files)
        var postsToUpdate = new ConcurrentBag<(int Id, string NewHash, long NewSize, DateTime NewMtime, bool HashChanged, string? FileIdentityDevice, string? FileIdentityValue)>();
        // Track new paths that match an existing file identity and may actually be moves.
        var potentialMoves = new ConcurrentBag<PotentialMoveCandidate>();
        var addedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int newPostCount = 0;

        status?.Report($"Scanning files...");
        _logger.LogInformation("Streaming files from {Path}...", directoryPath);

        int scanned = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _scanParallelism,
            CancellationToken = cancellationToken
        };

        var items = _mediaSource.GetItemsAsync(directoryPath, cancellationToken);

        await Parallel.ForEachAsync(items, parallelOptions, async (item, ct) =>
        {
            var added = await ProcessFileOptimizedAsync(
                library,
                item,
                existingPosts,
                existingPostsByIdentity,
                excludedPaths,
                ignoredPathPrefixes,
                seenPaths,
                postsToUpdate,
                potentialMoves,
                addedPaths,
                ct);
            if (added) Interlocked.Increment(ref newPostCount);
            Interlocked.Increment(ref scanned);

            if (scanned % 10 == 0 || scanned == total)
            {
                if (total > 0)
                {
                    progress?.Report((float)scanned / total * 80); // Reserve 20% for cleanup
                    status?.Report($"Scanning: {scanned}/{total} files");
                }
            }
        });

        await _ingestionService.FlushAsync(cancellationToken);

        // Resolve move/rename candidates before orphan cleanup.
        var moveResolution = ResolveMoveCandidates(potentialMoves, existingPostsByIdentity, seenPaths);
        var postsToMove = moveResolution.Moves;

        foreach (var unmatchedCandidate in moveResolution.UnmatchedCandidates)
        {
            var newItem = new MediaSourceItem
            {
                FullPath = unmatchedCandidate.FullPath,
                RelativePath = unmatchedCandidate.RelativePath,
                SizeBytes = unmatchedCandidate.SizeBytes,
                LastModifiedUtc = unmatchedCandidate.LastModifiedUtc
            };

            await EnqueuePostAsync(
                library,
                newItem,
                unmatchedCandidate.Hash,
                unmatchedCandidate.FileIdentityDevice,
                unmatchedCandidate.FileIdentityValue,
                cancellationToken);
            addedPaths.TryAdd(unmatchedCandidate.RelativePath, 0);
            newPostCount++;
        }

        if (moveResolution.UnmatchedCandidates.Count > 0)
        {
            await _ingestionService.FlushAsync(cancellationToken);
        }

        // Phase 2: Update changed files
        if (!postsToUpdate.IsEmpty || postsToMove.Count > 0)
        {
            var totalUpdates = postsToUpdate.Count + postsToMove.Count;
            status?.Report($"Updating {totalUpdates} posts ({postsToUpdate.Count} changed, {postsToMove.Count} moved)...");
            _logger.LogInformation(
                "Updating posts in library {Library}: {ChangedCount} changed, {MovedCount} moved",
                library.Name,
                postsToUpdate.Count,
                postsToMove.Count);

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

            foreach (var update in postsToUpdate)
            {
                var post = await dbContext.Posts.FindAsync(new object[] { update.Id }, cancellationToken);
                if (post != null)
                {
                    post.ContentHash = update.NewHash;
                    post.SizeBytes = update.NewSize;
                    post.FileModifiedDate = update.NewMtime;
                    post.FileIdentityDevice = update.FileIdentityDevice;
                    post.FileIdentityValue = update.FileIdentityValue;

                    if (update.HashChanged)
                    {
                        // Content changed; reset enrichment fields so they get reprocessed.
                        post.Width = 0;
                        post.Height = 0;
                        post.PerceptualHash = null;
                        post.PerceptualHashP = null;
                    }
                }
            }

            foreach (var move in postsToMove)
            {
                var post = await dbContext.Posts.FindAsync(new object[] { move.Id }, cancellationToken);
                if (post == null)
                {
                    continue;
                }

                post.RelativePath = move.NewRelativePath;
                post.ContentHash = move.Hash;
                post.SizeBytes = move.NewSize;
                post.FileModifiedDate = move.NewMtime;
                post.ContentType = SupportedMedia.GetMimeType(Path.GetExtension(move.NewRelativePath));
                post.FileIdentityDevice = move.FileIdentityDevice;
                post.FileIdentityValue = move.FileIdentityValue;

                _logger.LogInformation(
                    "Moved post {PostId}: {OldPath} -> {NewPath}",
                    move.Id,
                    move.OldRelativePath,
                    move.NewRelativePath);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var copiedTagCount = await CopyNonFolderTagsFromHashMatchesAsync(library.Id, addedPaths.Keys.ToList(), cancellationToken);
        if (copiedTagCount > 0)
        {
            _logger.LogInformation("Copied {Count} non-folder tag assignments onto newly added duplicate posts.", copiedTagCount);
        }

        // Phase 3: Remove orphaned posts (files deleted from disk)
        var orphanPaths = existingPosts.Keys
            .Where(p => !seenPaths.ContainsKey(p))
            .ToList();

        if (orphanPaths.Count > 0)
        {
            status?.Report($"Removing {orphanPaths.Count} orphaned posts...");
            _logger.LogInformation("Removing {Count} orphaned posts from library {Library}", orphanPaths.Count, library.Name);

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

            // Remove in batches to avoid huge IN clauses
            const int batchSize = 100;
            for (int i = 0; i < orphanPaths.Count; i += batchSize)
            {
                var batch = orphanPaths.Skip(i).Take(batchSize).ToList();
                var orphanIds = batch.Select(p => existingPosts[p].Id).ToList();

                await dbContext.Posts
                    .Where(p => orphanIds.Contains(p.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            _logger.LogInformation("Removed {Count} orphaned posts", orphanPaths.Count);
        }

        progress?.Report(100);
        status?.Report(
            $"Finished scanning {library.Name} — {scanned} files, {newPostCount} added, {postsToUpdate.Count} updated, {postsToMove.Count} moved, {orphanPaths.Count} orphans removed");

        return new ScanResult(scanned, newPostCount, postsToUpdate.Count, postsToMove.Count, orphanPaths.Count);
    }

    public async Task ProcessFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var relativePath = item.RelativePath;
        var existingPost = await dbContext.Posts.FirstOrDefaultAsync(p => p.LibraryId == library.Id && p.RelativePath == relativePath, cancellationToken);

        if (existingPost != null) return;

        var hash = await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash)) return;

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);

        await EnqueuePostAsync(library, item, hash, identity?.Device, identity?.Value, cancellationToken);
    }

    private async Task<bool> ProcessFileOptimizedAsync(
        Library library,
        MediaSourceItem item,
        Dictionary<string, ExistingPostInfo> existingPosts,
        IReadOnlyDictionary<string, List<ExistingPostInfo>> existingPostsByIdentity,
        HashSet<string> excludedPaths,
        HashSet<string> ignoredPathPrefixes,
        ConcurrentDictionary<string, byte> seenPaths,
        ConcurrentBag<(int Id, string NewHash, long NewSize, DateTime NewMtime, bool HashChanged, string? FileIdentityDevice, string? FileIdentityValue)> postsToUpdate,
        ConcurrentBag<PotentialMoveCandidate> potentialMoves,
        ConcurrentDictionary<string, byte> addedPaths,
        CancellationToken cancellationToken)
    {
        var relativePath = item.RelativePath;

        var normalizedRelativePath = RelativePathMatcher.NormalizePath(relativePath);
        if (ignoredPathPrefixes.Any(prefix => RelativePathMatcher.IsWithinPrefix(normalizedRelativePath, prefix)))
        {
            return false;
        }

        seenPaths.TryAdd(relativePath, 0);

        // Skip files on the exclusion list (e.g. duplicates resolved by user)
        if (excludedPaths.Contains(relativePath)) return false;

        // Check if file already exists in DB
        if (existingPosts.TryGetValue(relativePath, out var existing))
        {
            // Change detection: compare file size and mtime
            var fileChanged = item.SizeBytes != existing.SizeBytes
                           || Math.Abs((item.LastModifiedUtc - existing.FileModifiedDate).TotalSeconds) > 1;
            var missingIdentity = string.IsNullOrWhiteSpace(existing.FileIdentityDevice)
                || string.IsNullOrWhiteSpace(existing.FileIdentityValue);

            if (!fileChanged)
            {
                if (!missingIdentity)
                {
                    return false;
                }

                var existingPathIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
                var resolvedIdentityDevice = existingPathIdentity?.Device ?? existing.FileIdentityDevice;
                var resolvedIdentityValue = existingPathIdentity?.Value ?? existing.FileIdentityValue;

                var identityChanged = !string.Equals(existing.FileIdentityDevice, resolvedIdentityDevice, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.FileIdentityValue, resolvedIdentityValue, StringComparison.OrdinalIgnoreCase);

                if (!identityChanged)
                {
                    return false;
                }

                postsToUpdate.Add((
                    existing.Id,
                    existing.Hash,
                    existing.SizeBytes,
                    existing.FileModifiedDate,
                    false,
                    resolvedIdentityDevice,
                    resolvedIdentityValue));

                return false;
            }

            var changedPathIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
            var resolvedChangedIdentityDevice = changedPathIdentity?.Device ?? existing.FileIdentityDevice;
            var resolvedChangedIdentityValue = changedPathIdentity?.Value ?? existing.FileIdentityValue;

            // File has changed — re-hash and queue for update
            var newHash = await ComputeHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(newHash)) return false;

            var hashChanged = !string.Equals(newHash, existing.Hash, StringComparison.OrdinalIgnoreCase);

            postsToUpdate.Add((
                existing.Id,
                newHash,
                item.SizeBytes,
                item.LastModifiedUtc,
                hashChanged,
                resolvedChangedIdentityDevice,
                resolvedChangedIdentityValue));

            if (hashChanged)
            {
                _logger.LogInformation("File changed: {Path} (size: {OldSize}→{NewSize})", relativePath, existing.SizeBytes, item.SizeBytes);
            }

            return false;
        }

        // New file — hash and ingest
        var hash = await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash)) return false;

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        var identityKey = BuildIdentityKey(identity?.Device, identity?.Value);
        if (identityKey != null && existingPostsByIdentity.TryGetValue(identityKey, out var candidatesByIdentity) && candidatesByIdentity.Count > 0)
        {
            potentialMoves.Add(new PotentialMoveCandidate(
                item.FullPath,
                relativePath,
                hash,
                item.SizeBytes,
                item.LastModifiedUtc,
                identity?.Device,
                identity?.Value));
            return false;
        }

        await EnqueuePostAsync(library, item, hash, identity?.Device, identity?.Value, cancellationToken);
        addedPaths.TryAdd(relativePath, 0);
        return true;
    }

    private async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        return await _hasher.ComputeContentHashAsync(filePath, cancellationToken);
    }

    private async Task EnqueuePostAsync(
        Library library,
        MediaSourceItem item,
        string hash,
        string? fileIdentityDevice,
        string? fileIdentityValue,
        CancellationToken cancellationToken)
    {
        var contentType = SupportedMedia.GetMimeType(Path.GetExtension(item.RelativePath));

        var post = new Post
        {
            LibraryId = library.Id,
            RelativePath = item.RelativePath,
            ContentHash = hash,
            SizeBytes = item.SizeBytes,
            FileModifiedDate = item.LastModifiedUtc,
            FileIdentityDevice = fileIdentityDevice,
            FileIdentityValue = fileIdentityValue,
            ContentType = contentType,
            ImportDate = DateTime.UtcNow
        };

        await _ingestionService.EnqueuePostAsync(post, cancellationToken);
    }

    private static (List<MoveUpdate> Moves, List<PotentialMoveCandidate> UnmatchedCandidates) ResolveMoveCandidates(
        IEnumerable<PotentialMoveCandidate> potentialMoves,
        IReadOnlyDictionary<string, List<ExistingPostInfo>> existingPostsByIdentity,
        ConcurrentDictionary<string, byte> seenPaths)
    {
        var moves = new List<MoveUpdate>();
        var unmatched = new List<PotentialMoveCandidate>();
        var movedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedPostIds = new HashSet<int>();

        foreach (var candidate in potentialMoves)
        {
            var identityKey = BuildIdentityKey(candidate.FileIdentityDevice, candidate.FileIdentityValue);
            if (identityKey == null
                || !existingPostsByIdentity.TryGetValue(identityKey, out var candidatesByIdentity)
                || candidatesByIdentity.Count == 0)
            {
                unmatched.Add(candidate);
                continue;
            }

            var source = candidatesByIdentity.FirstOrDefault(existing =>
                !seenPaths.ContainsKey(existing.RelativePath)
                && !movedOldPaths.Contains(existing.RelativePath)
                && !movedPostIds.Contains(existing.Id));

            if (source == null)
            {
                unmatched.Add(candidate);
                continue;
            }

            moves.Add(new MoveUpdate(
                source.Id,
                source.RelativePath,
                candidate.RelativePath,
                candidate.Hash,
                candidate.SizeBytes,
                candidate.LastModifiedUtc,
                candidate.FileIdentityDevice,
                candidate.FileIdentityValue));

            movedOldPaths.Add(source.RelativePath);
            movedPostIds.Add(source.Id);

            // Mark old path as "seen" to prevent orphan deletion.
            seenPaths.TryAdd(source.RelativePath, 0);
        }

        return (moves, unmatched);
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
            .Where(p => p.LibraryId == libraryId && newRelativePaths.Contains(p.RelativePath))
            .Select(p => new { p.Id, p.ContentHash })
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
                && pt.Post.LibraryId == libraryId
                && hashSet.Contains(pt.Post.ContentHash))
            .Select(pt => new { pt.PostId, pt.Post.ContentHash, pt.TagId, pt.Source })
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
                if (existingSet.Add(row))
                {
                    dbContext.PostTags.Add(new PostTag
                    {
                        PostId = row.Id,
                        TagId = row.TagId,
                        Source = row.Source
                    });
                    inserted++;
                }
            }
        }

        if (inserted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return inserted;
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
