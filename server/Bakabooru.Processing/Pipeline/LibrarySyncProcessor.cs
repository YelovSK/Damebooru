using Bakabooru.Core;
using Bakabooru.Core.Config;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
using Bakabooru.Core.Paths;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Bakabooru.Processing.Pipeline;

public class LibrarySyncProcessor : ILibrarySyncProcessor
{
    private readonly ILogger<LibrarySyncProcessor> _logger;
    private readonly IHasherService _hasher;
    private readonly IPostIngestionService _ingestionService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaSource _mediaSource;
    private readonly int _scanParallelism;

    public LibrarySyncProcessor(
        ILogger<LibrarySyncProcessor> logger,
        IHasherService hasher,
        IPostIngestionService ingestionService,
        IServiceScopeFactory scopeFactory,
        IMediaSource mediaSource,
        IOptions<BakabooruConfig> options)
    {
        _logger = logger;
        _hasher = hasher;
        _ingestionService = ingestionService;
        _scopeFactory = scopeFactory;
        _mediaSource = mediaSource;
        _scanParallelism = Math.Max(1, options.Value.Scanner.Parallelism);
    }

    /// <summary>Snapshot of an existing post for fast in-memory comparison.</summary>
    private record ExistingPostInfo(int Id, string RelativePath, string Hash, long SizeBytes, DateTime FileModifiedDate);

    /// <summary>Potential file move/rename detected during scan (same content hash, new path).</summary>
    private record PotentialMoveCandidate(string RelativePath, string Hash, long SizeBytes, DateTime LastModifiedUtc);

    /// <summary>Resolved post move/rename that should update one existing post path.</summary>
    private record MoveUpdate(
        int Id,
        string OldRelativePath,
        string NewRelativePath,
        string Hash,
        long NewSize,
        DateTime NewMtime);

    public async Task ProcessDirectoryAsync(Library library, string directoryPath, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        status?.Report($"Counting files in {directoryPath}...");
        _logger.LogInformation("Counting files in {Path}...", directoryPath);
        var total = await _mediaSource.CountAsync(directoryPath, cancellationToken);
        _logger.LogInformation("Found {Count} files to process in library {Library}", total, library.Name);

        // Pre-load existing posts for comparison
        status?.Report($"Loading existing posts database...");
        _logger.LogInformation("Loading existing posts for library {Library}...", library.Name);

        Dictionary<string, ExistingPostInfo> existingPosts;
        Dictionary<string, List<ExistingPostInfo>> existingPostsByHash;
        ConcurrentDictionary<string, byte> knownHashes;
        HashSet<string> excludedPaths;
        HashSet<string> ignoredPathPrefixes;

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
            var libraryPosts = await dbContext.Posts
                .AsNoTracking()
                .Where(p => p.LibraryId == library.Id)
                .Select(p => new { p.Id, p.RelativePath, p.ContentHash, p.SizeBytes, p.FileModifiedDate })
                .ToListAsync(cancellationToken);

            var knownContentHashes = await dbContext.Posts
                .AsNoTracking()
                .Select(p => p.ContentHash)
                .Distinct()
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
                            first.FileModifiedDate);
                    },
                    StringComparer.OrdinalIgnoreCase);

            existingPostsByHash = libraryPosts
                .Where(p => !string.IsNullOrEmpty(p.ContentHash))
                .GroupBy(p => p.ContentHash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => new ExistingPostInfo(p.Id, p.RelativePath, p.ContentHash, p.SizeBytes, p.FileModifiedDate)).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            knownHashes = new ConcurrentDictionary<string, byte>(
                knownContentHashes
                    .Where(h => !string.IsNullOrEmpty(h))
                    .Select(h => new KeyValuePair<string, byte>(h!, 0)),
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
        _logger.LogInformation("Loaded {Count} existing posts and {HashCount} unique hashes.", existingPosts.Count, knownHashes.Count);

        // Track which paths we see on disk for orphan detection
        var seenPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Track posts that need updating (changed files)
        var postsToUpdate = new ConcurrentBag<(int Id, string NewHash, long NewSize, DateTime NewMtime, bool HashChanged)>();
        // Track new paths that were deduplicated by hash and may actually be moves.
        var potentialMoves = new ConcurrentBag<PotentialMoveCandidate>();

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
            await ProcessFileOptimizedAsync(
                library,
                item,
                existingPosts,
                knownHashes,
                excludedPaths,
                ignoredPathPrefixes,
                seenPaths,
                postsToUpdate,
                potentialMoves,
                ct);
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
        var postsToMove = ResolveMoveCandidates(potentialMoves, existingPostsByHash, seenPaths);

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
            var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();

            foreach (var update in postsToUpdate)
            {
                var post = await dbContext.Posts.FindAsync(new object[] { update.Id }, cancellationToken);
                if (post != null)
                {
                    post.ContentHash = update.NewHash;
                    post.SizeBytes = update.NewSize;
                    post.FileModifiedDate = update.NewMtime;

                    if (update.HashChanged)
                    {
                        // Content changed; reset enrichment fields so they get reprocessed.
                        post.Width = 0;
                        post.Height = 0;
                        post.PerceptualHash = null;
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

                _logger.LogInformation(
                    "Moved post {PostId}: {OldPath} -> {NewPath}",
                    move.Id,
                    move.OldRelativePath,
                    move.NewRelativePath);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
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
            var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();

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
            $"Finished scanning {library.Name} — {scanned} files, {postsToUpdate.Count} updated, {postsToMove.Count} moved, {orphanPaths.Count} orphans removed");
    }

    public async Task ProcessFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        var relativePath = item.RelativePath;
        var existingPost = await dbContext.Posts.FirstOrDefaultAsync(p => p.LibraryId == library.Id && p.RelativePath == relativePath, cancellationToken);

        if (existingPost != null) return;

        var hash = await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash)) return;

        var isDuplicate = await dbContext.Posts.AnyAsync(p => p.ContentHash == hash, cancellationToken);
        if (isDuplicate) return;

        await EnqueuePostAsync(library, item, hash, cancellationToken);
    }

    private async Task ProcessFileOptimizedAsync(
        Library library,
        MediaSourceItem item,
        Dictionary<string, ExistingPostInfo> existingPosts,
        ConcurrentDictionary<string, byte> knownHashes,
        HashSet<string> excludedPaths,
        HashSet<string> ignoredPathPrefixes,
        ConcurrentDictionary<string, byte> seenPaths,
        ConcurrentBag<(int Id, string NewHash, long NewSize, DateTime NewMtime, bool HashChanged)> postsToUpdate,
        ConcurrentBag<PotentialMoveCandidate> potentialMoves,
        CancellationToken cancellationToken)
    {
        var relativePath = item.RelativePath;

        var normalizedRelativePath = RelativePathMatcher.NormalizePath(relativePath);
        if (ignoredPathPrefixes.Any(prefix => RelativePathMatcher.IsWithinPrefix(normalizedRelativePath, prefix)))
        {
            return;
        }

        seenPaths.TryAdd(relativePath, 0);

        // Skip files on the exclusion list (e.g. duplicates resolved by user)
        if (excludedPaths.Contains(relativePath)) return;

        // Check if file already exists in DB
        if (existingPosts.TryGetValue(relativePath, out var existing))
        {
            // Change detection: compare file size and mtime
            var fileChanged = item.SizeBytes != existing.SizeBytes
                           || Math.Abs((item.LastModifiedUtc - existing.FileModifiedDate).TotalSeconds) > 1;

            if (!fileChanged) return; // File unchanged, skip

            // File has changed — re-hash and queue for update
            var newHash = await ComputeHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(newHash)) return;

            var hashChanged = !string.Equals(newHash, existing.Hash, StringComparison.OrdinalIgnoreCase);
            postsToUpdate.Add((existing.Id, newHash, item.SizeBytes, item.LastModifiedUtc, hashChanged));

            if (hashChanged)
            {
                _logger.LogInformation("File changed: {Path} (size: {OldSize}→{NewSize})", relativePath, existing.SizeBytes, item.SizeBytes);

                // Update the known hash set
                knownHashes.TryAdd(newHash, 0);
            }
            return;
        }

        // New file — hash and ingest
        var hash = await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash)) return;

        // Check global deduplication
        if (!knownHashes.TryAdd(hash, 0))
        {
            potentialMoves.Add(new PotentialMoveCandidate(relativePath, hash, item.SizeBytes, item.LastModifiedUtc));
            _logger.LogDebug("Skipping duplicate content {Hash} at {Path}", hash, relativePath);
            return;
        }

        await EnqueuePostAsync(library, item, hash, cancellationToken);
    }

    private async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        return await _hasher.ComputeContentHashAsync(filePath, cancellationToken);
    }

    private async Task EnqueuePostAsync(Library library, MediaSourceItem item, string hash, CancellationToken cancellationToken)
    {
        var contentType = SupportedMedia.GetMimeType(Path.GetExtension(item.RelativePath));

        var post = new Post
        {
            LibraryId = library.Id,
            RelativePath = item.RelativePath,
            ContentHash = hash,
            SizeBytes = item.SizeBytes,
            FileModifiedDate = item.LastModifiedUtc,
            ContentType = contentType,
            ImportDate = DateTime.UtcNow
        };

        await _ingestionService.EnqueuePostAsync(post, cancellationToken);
    }

    private static List<MoveUpdate> ResolveMoveCandidates(
        IEnumerable<PotentialMoveCandidate> potentialMoves,
        IReadOnlyDictionary<string, List<ExistingPostInfo>> existingPostsByHash,
        ConcurrentDictionary<string, byte> seenPaths)
    {
        var moves = new List<MoveUpdate>();
        var movedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedPostIds = new HashSet<int>();

        foreach (var candidate in potentialMoves)
        {
            if (!existingPostsByHash.TryGetValue(candidate.Hash, out var candidatesByHash) || candidatesByHash.Count == 0)
            {
                continue;
            }

            var source = candidatesByHash.FirstOrDefault(existing =>
                !seenPaths.ContainsKey(existing.RelativePath)
                && !movedOldPaths.Contains(existing.RelativePath)
                && !movedPostIds.Contains(existing.Id));

            if (source == null)
            {
                continue;
            }

            moves.Add(new MoveUpdate(
                source.Id,
                source.RelativePath,
                candidate.RelativePath,
                candidate.Hash,
                candidate.SizeBytes,
                candidate.LastModifiedUtc));

            movedOldPaths.Add(source.RelativePath);
            movedPostIds.Add(source.Id);

            // Mark old path as "seen" to prevent orphan deletion.
            seenPaths.TryAdd(source.RelativePath, 0);
        }

        return moves;
    }
}
