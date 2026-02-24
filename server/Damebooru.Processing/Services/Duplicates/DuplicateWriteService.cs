using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Paths;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Duplicates;

public class DuplicateWriteService
{
    private sealed record SameFolderPartition(List<Post> Posts);

    private readonly DamebooruDbContext _context;
    private readonly DuplicateReadService _duplicateReadService;

    public DuplicateWriteService(DamebooruDbContext context, DuplicateReadService duplicateReadService)
    {
        _context = context;
        _duplicateReadService = duplicateReadService;
    }

    public async Task<Result> KeepAllAsync(int groupId)
    {
        var group = await _context.DuplicateGroups.FindAsync(new object[] { groupId });
        if (group == null)
        {
            return Result.Failure(OperationError.NotFound, "Group not found.");
        }

        group.IsResolved = true;
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result> MarkUnresolvedAsync(int groupId, CancellationToken cancellationToken = default)
    {
        var group = await _context.DuplicateGroups
            .Include(g => g.Entries)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group == null)
        {
            return Result.Failure(OperationError.NotFound, "Group not found.");
        }

        if (!group.IsResolved)
        {
            return Result.Success();
        }

        group.IsResolved = false;
        await _context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<MarkAllUnresolvedResponseDto> MarkAllUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _context.DuplicateGroups
            .Where(g => g.IsResolved)
            .Include(g => g.Entries)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var group in groups)
        {
            group.IsResolved = false;
            changed++;
        }

        if (changed > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new MarkAllUnresolvedResponseDto { Unresolved = changed };
    }

    public async Task<Result> KeepOneAsync(int groupId, int postId)
    {
        var group = await _context.DuplicateGroups
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.PostTags)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Sources)
            .FirstOrDefaultAsync(g => g.Id == groupId);

        if (group == null)
        {
            return Result.Failure(OperationError.NotFound, "Group not found.");
        }

        var keptEntry = group.Entries.FirstOrDefault(e => e.PostId == postId);
        if (keptEntry == null)
        {
            return Result.Failure(OperationError.InvalidInput, "Post is not a member of this group.");
        }

        await ResolveGroupKeepingPostAsync(group, postId);
        return Result.Success();
    }

    public async Task<Result> AutoResolveGroupAsync(int groupId, CancellationToken cancellationToken = default)
    {
        var group = await _context.DuplicateGroups
            .Where(g => g.Id == groupId && !g.IsResolved)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.PostTags)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Sources)
            .FirstOrDefaultAsync(cancellationToken);

        if (group == null)
        {
            return Result.Failure(OperationError.NotFound, "Duplicate group not found.");
        }

        if (group.Entries.Count < 2)
        {
            _context.DuplicateGroups.Remove(group);
            await _context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var keepPostId = SelectBestQualityPostId(group.Entries.Select(e => e.Post));
        await ResolveGroupKeepingPostAsync(group, keepPostId);
        return Result.Success();
    }

    public async Task<ResolveAllExactResponseDto> ResolveAllExactAsync()
    {
        var exactGroups = await _context.DuplicateGroups
            .Where(g => !g.IsResolved && g.Type == DuplicateType.Exact)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.PostTags)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Sources)
            .ToListAsync();

        if (exactGroups.Count == 0)
        {
            return new ResolveAllExactResponseDto { Resolved = 0 };
        }

        var resolved = 0;
        foreach (var group in exactGroups)
        {
            var keepPostId = SelectBestQualityPostId(group.Entries.Select(e => e.Post));
            await ResolveGroupKeepingPostAsync(group, keepPostId, saveChanges: false);
            resolved++;
        }

        await _context.SaveChangesAsync();
        return new ResolveAllExactResponseDto { Resolved = resolved };
    }

    public async Task<ResolveAllExactResponseDto> ResolveAllAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _context.DuplicateGroups
            .Where(g => !g.IsResolved)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.PostTags)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Sources)
            .ToListAsync(cancellationToken);

        if (groups.Count == 0)
        {
            return new ResolveAllExactResponseDto { Resolved = 0 };
        }

        var resolved = 0;
        foreach (var group in groups)
        {
            if (group.Entries.Count < 2)
            {
                _context.DuplicateGroups.Remove(group);
                continue;
            }

            var keepPostId = SelectBestQualityPostId(group.Entries.Select(e => e.Post));
            await ResolveGroupKeepingPostAsync(group, keepPostId, saveChanges: false);
            resolved++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new ResolveAllExactResponseDto { Resolved = resolved };
    }

    public async Task<Result<ResolveSameFolderResponseDto>> ResolveSameFolderGroupAsync(
        ResolveSameFolderGroupRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request.ParentDuplicateGroupId <= 0 || request.LibraryId <= 0)
        {
            return Result<ResolveSameFolderResponseDto>.Failure(OperationError.InvalidInput, "Invalid request payload.");
        }

        var partitionResult = await LoadSameFolderPartitionAsync(
            request.ParentDuplicateGroupId,
            request.LibraryId,
            request.FolderPath,
            cancellationToken);

        if (!partitionResult.IsSuccess)
        {
            return Result<ResolveSameFolderResponseDto>.Failure(
                partitionResult.Error ?? OperationError.InvalidInput,
                partitionResult.Message ?? "Request failed.");
        }

        return await ResolveSameFolderPartitionAsync(partitionResult.Value!, cancellationToken);
    }

    public async Task<Result<ResolveSameFolderResponseDto>> ResolveAllSameFolderAsync(bool exactOnly = false, CancellationToken cancellationToken = default)
    {
        var groups = await _duplicateReadService.GetSameFolderDuplicateGroupsAsync(cancellationToken);
        if (exactOnly)
        {
            groups = groups.Where(g => g.DuplicateType == DuplicateType.Exact).ToList();
        }

        if (groups.Count == 0)
        {
            return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto());
        }

        var summary = new ResolveSameFolderResponseDto();
        foreach (var group in groups)
        {
            var partitionResult = await LoadSameFolderPartitionAsync(
                group.ParentDuplicateGroupId,
                group.LibraryId,
                group.FolderPath,
                cancellationToken);

            if (!partitionResult.IsSuccess)
            {
                if (partitionResult.Error == OperationError.NotFound || partitionResult.Error == OperationError.InvalidInput)
                {
                    summary.SkippedGroups++;
                    continue;
                }

                return Result<ResolveSameFolderResponseDto>.Failure(
                    partitionResult.Error ?? OperationError.InvalidInput,
                    partitionResult.Message ?? "Request failed.");
            }

            var resolveResult = await ResolveSameFolderPartitionAsync(partitionResult.Value!, cancellationToken);
            if (!resolveResult.IsSuccess)
            {
                return resolveResult;
            }

            summary.ResolvedGroups += resolveResult.Value!.ResolvedGroups;
            summary.DeletedPosts += resolveResult.Value.DeletedPosts;
            summary.SkippedGroups += resolveResult.Value.SkippedGroups;
        }

        return Result<ResolveSameFolderResponseDto>.Success(summary);
    }

    public async Task<Result> ExcludeDuplicatePostAsync(int groupId, int postId, CancellationToken cancellationToken = default)
    {
        var group = await _context.DuplicateGroups
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group == null)
        {
            return Result.Failure(OperationError.NotFound, "Duplicate group not found.");
        }

        var entryToExclude = group.Entries.FirstOrDefault(e => e.PostId == postId);
        if (entryToExclude == null)
        {
            return Result.Failure(OperationError.NotFound, "Post is not in this duplicate group.");
        }

        if (group.Entries.Count < 2)
        {
            return Result.Failure(OperationError.InvalidInput, "Cannot exclude the last remaining post in a duplicate group.");
        }

        var postToExclude = entryToExclude.Post;

        var alreadyExcluded = await _context.ExcludedFiles.AnyAsync(
            e => e.LibraryId == postToExclude.LibraryId && e.RelativePath == postToExclude.RelativePath,
            cancellationToken);

        var affectedGroupIds = await CollectAffectedGroupIdsAsync([postId], cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);

        if (!alreadyExcluded)
        {
            _context.ExcludedFiles.Add(new ExcludedFile
            {
                LibraryId = postToExclude.LibraryId,
                RelativePath = postToExclude.RelativePath,
                ContentHash = postToExclude.ContentHash,
                ExcludedDate = DateTime.UtcNow,
                Reason = "duplicate_resolution"
            });
        }

        _context.Posts.Remove(postToExclude);
        await _context.SaveChangesAsync(CancellationToken.None);

        await ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result.Success();
    }

    public async Task<Result> DeleteDuplicatePostAsync(int groupId, int postId, CancellationToken cancellationToken = default)
    {
        var group = await _context.DuplicateGroups
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group == null)
        {
            return Result.Failure(OperationError.NotFound, "Duplicate group not found.");
        }

        var entryToDelete = group.Entries.FirstOrDefault(e => e.PostId == postId);
        if (entryToDelete == null)
        {
            return Result.Failure(OperationError.NotFound, "Post is not in this duplicate group.");
        }

        if (group.Entries.Count < 2)
        {
            return Result.Failure(OperationError.InvalidInput, "Cannot delete the last remaining post in a duplicate group.");
        }

        var postToDelete = entryToDelete.Post;

        if (!HasSameFolderPeer(group.Entries.Select(e => e.Post), postToDelete))
        {
            return Result.Failure(OperationError.InvalidInput, "File deletion is only allowed when at least one duplicate in the same folder exists.");
        }

        var affectedGroupIds = await CollectAffectedGroupIdsAsync([postId], cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);

        var deleteResult = DeletePostFromDiskAndDb(postToDelete);
        if (!deleteResult.IsSuccess)
        {
            return deleteResult;
        }

        await _context.SaveChangesAsync(CancellationToken.None);
        await ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result.Success();
    }

    public async Task<Result> UnexcludeFileAsync(int excludedFileId)
    {
        var entry = await _context.ExcludedFiles.FindAsync(new object[] { excludedFileId });
        if (entry == null)
        {
            return Result.Failure(OperationError.NotFound, "Excluded file not found.");
        }

        _context.ExcludedFiles.Remove(entry);
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<int> UnexcludeAllFilesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ExcludedFiles.ExecuteDeleteAsync(cancellationToken);
    }

    private Result DeletePostFromDiskAndDb(Post post)
    {
        if (!SafeSubpathResolver.TryResolve(post.Library.Path, post.RelativePath, out var fullPath))
        {
            return Result.Failure(OperationError.InvalidInput, "Invalid file path.");
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(OperationError.Conflict, $"Failed to delete file from disk: {ex.Message}");
        }

        var excluded = _context.ExcludedFiles
            .Where(e => e.LibraryId == post.LibraryId && e.RelativePath == post.RelativePath);
        _context.ExcludedFiles.RemoveRange(excluded);
        _context.Posts.Remove(post);

        return Result.Success();
    }

    private async Task ResolveGroupKeepingPostAsync(DuplicateGroup group, int keepPostId, bool saveChanges = true)
    {
        var keptEntry = group.Entries.First(e => e.PostId == keepPostId);
        var keptPost = keptEntry.Post;
        var removedEntries = group.Entries.Where(e => e.PostId != keepPostId).ToList();

        var existingTagAssignments = new HashSet<(int TagId, PostTagSource Source)>(
            keptPost.PostTags.Select(pt => (pt.TagId, pt.Source)));
        var existingSourceUrls = new HashSet<string>(
            keptPost.Sources.Select(s => s.Url),
            StringComparer.OrdinalIgnoreCase);
        var maxSourceOrder = keptPost.Sources.Count > 0
            ? keptPost.Sources.Max(s => s.Order)
            : -1;

        foreach (var entry in removedEntries)
        {
            var post = entry.Post;

            foreach (var pt in post.PostTags)
            {
                if (existingTagAssignments.Add((pt.TagId, pt.Source)))
                {
                    _context.PostTags.Add(new PostTag
                    {
                        PostId = keepPostId,
                        TagId = pt.TagId,
                        Source = pt.Source,
                    });
                }
            }

            foreach (var source in post.Sources)
            {
                if (existingSourceUrls.Add(source.Url))
                {
                    maxSourceOrder++;
                    _context.Set<PostSource>().Add(new PostSource
                    {
                        PostId = keepPostId,
                        Url = source.Url,
                        Order = maxSourceOrder,
                    });
                }
            }

            var alreadyExcluded = await _context.ExcludedFiles.AnyAsync(
                e => e.LibraryId == post.LibraryId && e.RelativePath == post.RelativePath);

            if (!alreadyExcluded)
            {
                _context.ExcludedFiles.Add(new ExcludedFile
                {
                    LibraryId = post.LibraryId,
                    RelativePath = post.RelativePath,
                    ContentHash = post.ContentHash,
                    ExcludedDate = DateTime.UtcNow,
                    Reason = "duplicate_resolution"
                });
            }

            _context.Posts.Remove(post);
        }

        _context.DuplicateGroups.Remove(group);

        if (saveChanges)
        {
            await _context.SaveChangesAsync();
        }
    }

    private async Task<Result<SameFolderPartition>> LoadSameFolderPartitionAsync(
        int parentDuplicateGroupId,
        int libraryId,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var group = await _context.DuplicateGroups
            .Where(g => g.Id == parentDuplicateGroupId && !g.IsResolved)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .FirstOrDefaultAsync(cancellationToken);

        if (group == null)
        {
            return Result<SameFolderPartition>.Failure(OperationError.NotFound, "Duplicate group not found.");
        }

        var normalizedFolderPath = DuplicatePathHelper.NormalizeFolderPath(folderPath);
        var partitionPosts = group.Entries
            .Select(e => e.Post)
            .Where(p => p.LibraryId == libraryId && DuplicatePathHelper.GetParentFolderPath(p.RelativePath) == normalizedFolderPath)
            .ToList();

        if (partitionPosts.Count < 2)
        {
            return Result<SameFolderPartition>.Failure(OperationError.InvalidInput, "Same-folder partition no longer has at least two posts.");
        }

        return Result<SameFolderPartition>.Success(new SameFolderPartition(partitionPosts));
    }

    private int SelectBestQualityPostId(IEnumerable<Post> posts)
    {
        return posts
            .OrderByDescending(p => (long)p.Width * p.Height)
            .ThenByDescending(p => p.SizeBytes)
            .ThenByDescending(p => p.FileModifiedDate)
            .ThenByDescending(p => p.Id)
            .Select(p => p.Id)
            .First();
    }

    private bool HasSameFolderPeer(IEnumerable<Post> posts, Post target)
    {
        var targetFolderPath = DuplicatePathHelper.GetParentFolderPath(target.RelativePath);
        return posts.Any(post =>
            post.Id != target.Id
            && post.LibraryId == target.LibraryId
            && DuplicatePathHelper.GetParentFolderPath(post.RelativePath) == targetFolderPath);
    }

    private async Task<List<int>> CollectAffectedGroupIdsAsync(IReadOnlyCollection<int> postIds, CancellationToken cancellationToken)
    {
        if (postIds.Count == 0)
        {
            return [];
        }

        return await _context.DuplicateGroups
            .Where(g => !g.IsResolved && g.Entries.Any(e => postIds.Contains(e.PostId)))
            .Select(g => g.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task ReconcileDuplicateGroupsAsync(IReadOnlyCollection<int> groupIds, CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0)
        {
            return;
        }

        var groupsToRemove = await _context.DuplicateGroups
            .Where(g => !g.IsResolved && groupIds.Contains(g.Id))
            .Where(g => g.Entries.Count < 2)
            .ToListAsync(cancellationToken);

        if (groupsToRemove.Count > 0)
        {
            _context.DuplicateGroups.RemoveRange(groupsToRemove);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Result<ResolveSameFolderResponseDto>> ResolveSameFolderPartitionAsync(
        SameFolderPartition partition,
        CancellationToken cancellationToken)
    {
        if (partition.Posts.Count < 2)
        {
            return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto
            {
                SkippedGroups = 1
            });
        }

        var keepPostId = SelectBestQualityPostId(partition.Posts);
        var postIdsToDelete = partition.Posts
            .Where(p => p.Id != keepPostId)
            .Select(p => p.Id)
            .ToList();

        if (postIdsToDelete.Count == 0)
        {
            return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto
            {
                SkippedGroups = 1
            });
        }

        var affectedGroupIds = await CollectAffectedGroupIdsAsync(postIdsToDelete, cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);
        var deleted = 0;
        foreach (var post in partition.Posts.Where(p => p.Id != keepPostId))
        {
            var deleteResult = DeletePostFromDiskAndDb(post);
            if (!deleteResult.IsSuccess)
            {
                return Result<ResolveSameFolderResponseDto>.Failure(
                    deleteResult.Error ?? OperationError.InvalidInput,
                    deleteResult.Message ?? "Request failed.");
            }

            deleted++;
        }

        await _context.SaveChangesAsync(CancellationToken.None);
        await ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto
        {
            ResolvedGroups = 1,
            DeletedPosts = deleted,
        });
    }

}
