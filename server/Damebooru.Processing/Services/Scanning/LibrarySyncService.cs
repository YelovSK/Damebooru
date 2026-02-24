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
    private sealed record ExistingPostInfo(
        int Id,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime FileModifiedDate,
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
        int Id,
        string OldRelativePath,
        string NewRelativePath,
        string Hash,
        long NewSize,
        DateTime NewMtime,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed record PostUpdateCandidate(
        int Id,
        string NewHash,
        long NewSize,
        DateTime NewMtime,
        bool HashChanged,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private sealed class SyncRunState
    {
        public required Dictionary<string, ExistingPostInfo> ExistingPosts { get; init; }
        public required Dictionary<string, List<ExistingPostInfo>> ExistingPostsByIdentity { get; init; }
        public required Dictionary<string, string> ExcludedHashesByPath { get; init; }
        public required HashSet<string> IgnoredPathPrefixes { get; init; }

        public ConcurrentDictionary<string, byte> SeenPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<PostUpdateCandidate> PostsToUpdate { get; } = [];
        public ConcurrentBag<PotentialMoveCandidate> PotentialMoves { get; } = [];
        public ConcurrentDictionary<string, byte> AddedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record MoveResolution(List<MoveUpdate> Moves, List<PotentialMoveCandidate> UnmatchedCandidates);

    private readonly ILogger<LibrarySyncService> _logger;
    private readonly IHasherService _hasher;
    private readonly IPostIngestionService _ingestionService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaSource _mediaSource;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly int _scanParallelism;

    public LibrarySyncService(
        ILogger<LibrarySyncService> logger,
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
            "Loaded {Count} existing posts and {IdentityCount} identity buckets.",
            state.ExistingPosts.Count,
            state.ExistingPostsByIdentity.Count);

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
            var added = await ProcessFileOptimizedAsync(library, item, state, ct);
            if (added)
            {
                Interlocked.Increment(ref addedCount);
            }

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

        await _ingestionService.FlushAsync(cancellationToken);

        var moveResolution = ResolveMoveCandidates(state);
        var movedPosts = moveResolution.Moves;

        foreach (var unmatchedCandidate in moveResolution.UnmatchedCandidates)
        {
            var item = new MediaSourceItem
            {
                FullPath = unmatchedCandidate.FullPath,
                RelativePath = unmatchedCandidate.RelativePath,
                SizeBytes = unmatchedCandidate.SizeBytes,
                LastModifiedUtc = unmatchedCandidate.LastModifiedUtc,
            };

            await EnqueuePostAsync(
                library,
                item,
                unmatchedCandidate.Hash,
                unmatchedCandidate.FileIdentityDevice,
                unmatchedCandidate.FileIdentityValue,
                cancellationToken);

            state.AddedPaths.TryAdd(unmatchedCandidate.RelativePath, 0);
            addedCount++;
        }

        if (moveResolution.UnmatchedCandidates.Count > 0)
        {
            await _ingestionService.FlushAsync(cancellationToken);
        }

        if (!state.PostsToUpdate.IsEmpty || movedPosts.Count > 0)
        {
            var totalUpdates = state.PostsToUpdate.Count + movedPosts.Count;
            status?.Report($"Updating {totalUpdates} posts ({state.PostsToUpdate.Count} changed, {movedPosts.Count} moved)...");

            _logger.LogInformation(
                "Updating posts in library {Library}: {ChangedCount} changed, {MovedCount} moved",
                library.Name,
                state.PostsToUpdate.Count,
                movedPosts.Count);

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

            foreach (var update in state.PostsToUpdate)
            {
                var post = await dbContext.Posts.FindAsync(new object[] { update.Id }, cancellationToken);
                if (post == null)
                {
                    continue;
                }

                post.ContentHash = update.NewHash;
                post.SizeBytes = update.NewSize;
                post.FileModifiedDate = update.NewMtime;
                post.FileIdentityDevice = update.FileIdentityDevice;
                post.FileIdentityValue = update.FileIdentityValue;

                if (update.HashChanged)
                {
                    post.Width = 0;
                    post.Height = 0;
                    post.PdqHash256 = null;
                }
            }

            foreach (var move in movedPosts)
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

        var copiedTags = await CopyNonFolderTagsFromHashMatchesAsync(library.Id, state.AddedPaths.Keys.ToList(), cancellationToken);
        if (copiedTags > 0)
        {
            _logger.LogInformation("Copied {Count} non-folder tag assignments onto newly added duplicate posts.", copiedTags);
        }

        var orphanPaths = state.ExistingPosts.Keys
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
                var orphanIds = batch.Select(path => state.ExistingPosts[path].Id).ToList();

                await dbContext.Posts
                    .Where(p => orphanIds.Contains(p.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            _logger.LogInformation("Removed {Count} orphaned posts", orphanPaths.Count);
        }

        progress?.Report(100);
        status?.Report($"Finished scanning {library.Name} - {scanned} files, {addedCount} added, {state.PostsToUpdate.Count} updated, {movedPosts.Count} moved, {orphanPaths.Count} orphans removed");

        return new ScanResult(scanned, addedCount, state.PostsToUpdate.Count, movedPosts.Count, orphanPaths.Count);
    }

    public async Task ProcessFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var existingPost = await dbContext.Posts
            .FirstOrDefaultAsync(p => p.LibraryId == library.Id && p.RelativePath == item.RelativePath, cancellationToken);

        if (existingPost != null)
        {
            return;
        }

        var hash = await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        await EnqueuePostAsync(library, item, hash, identity?.Device, identity?.Value, cancellationToken);
    }

    private async Task<SyncRunState> LoadStateAsync(int libraryId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var libraryPosts = await dbContext.Posts
            .AsNoTracking()
            .Where(p => p.LibraryId == libraryId)
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

        var existingPosts = libraryPosts
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

        var existingPostsByIdentity = libraryPosts
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
            ExistingPosts = existingPosts,
            ExistingPostsByIdentity = existingPostsByIdentity,
            ExcludedHashesByPath = excludedHashesByPath,
            IgnoredPathPrefixes = ignoredPathPrefixes
        };
    }

    private async Task<bool> ProcessFileOptimizedAsync(Library library, MediaSourceItem item, SyncRunState state, CancellationToken cancellationToken)
    {
        var relativePath = item.RelativePath;
        var normalizedRelativePath = RelativePathMatcher.NormalizePath(relativePath);
        if (state.IgnoredPathPrefixes.Any(prefix => RelativePathMatcher.IsWithinPrefix(normalizedRelativePath, prefix)))
        {
            return false;
        }

        state.SeenPaths.TryAdd(relativePath, 0);

        string? precomputedHash = null;
        if (state.ExcludedHashesByPath.TryGetValue(relativePath, out var excludedHash))
        {
            var currentHash = await ComputeHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(currentHash))
            {
                return false;
            }

            precomputedHash = currentHash;
            if (string.Equals(currentHash, excludedHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _logger.LogInformation("Exclusion mismatch for {Path}: path matched but hash changed, allowing ingest", relativePath);
        }

        if (state.ExistingPosts.TryGetValue(relativePath, out var existing))
        {
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

                var resolvedIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
                var newDevice = resolvedIdentity?.Device ?? existing.FileIdentityDevice;
                var newValue = resolvedIdentity?.Value ?? existing.FileIdentityValue;

                var identityChanged = !string.Equals(existing.FileIdentityDevice, newDevice, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.FileIdentityValue, newValue, StringComparison.OrdinalIgnoreCase);

                if (!identityChanged)
                {
                    return false;
                }

                state.PostsToUpdate.Add(new PostUpdateCandidate(
                    existing.Id,
                    existing.Hash,
                    existing.SizeBytes,
                    existing.FileModifiedDate,
                    false,
                    newDevice,
                    newValue));

                return false;
            }

            var changedIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
            var changedDevice = changedIdentity?.Device ?? existing.FileIdentityDevice;
            var changedValue = changedIdentity?.Value ?? existing.FileIdentityValue;

            var newHash = precomputedHash ?? await ComputeHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(newHash))
            {
                return false;
            }

            var hashChanged = !string.Equals(newHash, existing.Hash, StringComparison.OrdinalIgnoreCase);
            state.PostsToUpdate.Add(new PostUpdateCandidate(
                existing.Id,
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

            return false;
        }

        var hash = precomputedHash ?? await ComputeHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        var identityKey = BuildIdentityKey(identity?.Device, identity?.Value);
        if (identityKey != null
            && state.ExistingPostsByIdentity.TryGetValue(identityKey, out var candidatesByIdentity)
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
            return false;
        }

        await EnqueuePostAsync(library, item, hash, identity?.Device, identity?.Value, cancellationToken);
        state.AddedPaths.TryAdd(relativePath, 0);
        return true;
    }

    private MoveResolution ResolveMoveCandidates(SyncRunState state)
    {
        var moves = new List<MoveUpdate>();
        var unmatched = new List<PotentialMoveCandidate>();
        var movedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedPostIds = new HashSet<int>();

        foreach (var candidate in state.PotentialMoves)
        {
            var identityKey = BuildIdentityKey(candidate.FileIdentityDevice, candidate.FileIdentityValue);
            if (identityKey == null
                || !state.ExistingPostsByIdentity.TryGetValue(identityKey, out var candidatesByIdentity)
                || candidatesByIdentity.Count == 0)
            {
                unmatched.Add(candidate);
                continue;
            }

            var source = candidatesByIdentity.FirstOrDefault(existing =>
                !state.SeenPaths.ContainsKey(existing.RelativePath)
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
            ImportDate = DateTime.UtcNow,
        };

        await _ingestionService.EnqueuePostAsync(post, cancellationToken);
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
