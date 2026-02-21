using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class DuplicateService
{
    private readonly DamebooruDbContext _context;
    private readonly DuplicateQueryService _duplicateQueryService;
    private readonly DuplicateMutationSupportService _duplicateMutationSupportService;

    public DuplicateService(
        DamebooruDbContext context,
        DuplicateQueryService duplicateQueryService,
        DuplicateMutationSupportService duplicateMutationSupportService)
    {
        _context = context;
        _duplicateQueryService = duplicateQueryService;
        _duplicateMutationSupportService = duplicateMutationSupportService;
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

        await _duplicateMutationSupportService.ResolveGroupKeepingPostAsync(group, postId);
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

        var keepPostId = _duplicateMutationSupportService.SelectBestQualityPostId(group.Entries.Select(e => e.Post));
        await _duplicateMutationSupportService.ResolveGroupKeepingPostAsync(group, keepPostId);
        return Result.Success();
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
            e => e.LibraryId == postToExclude.LibraryId && e.RelativePath == postToExclude.RelativePath, cancellationToken);

        var affectedGroupIds = await _duplicateMutationSupportService.CollectAffectedGroupIdsAsync([postId], cancellationToken);

        // Commit zone: do not allow cooperative cancellation once mutation begins.
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

        await _duplicateMutationSupportService.ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result.Success();
    }

    public async Task<ResolveAllExactResponseDto> ResolveAllExactAsync()
    {
        var exactGroups = await _context.DuplicateGroups
            .Where(g => !g.IsResolved && g.Type == "exact")
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
            var keepPostId = _duplicateMutationSupportService.SelectBestQualityPostId(group.Entries.Select(e => e.Post));

            await _duplicateMutationSupportService.ResolveGroupKeepingPostAsync(group, keepPostId, saveChanges: false);
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

            var keepPostId = _duplicateMutationSupportService.SelectBestQualityPostId(group.Entries.Select(e => e.Post));
            await _duplicateMutationSupportService.ResolveGroupKeepingPostAsync(group, keepPostId, saveChanges: false);
            resolved++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new ResolveAllExactResponseDto { Resolved = resolved };
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

        if (!_duplicateMutationSupportService.HasSameFolderPeer(group.Entries.Select(e => e.Post), postToDelete))
        {
            return Result.Failure(OperationError.InvalidInput, "File deletion is only allowed when at least one duplicate in the same folder exists.");
        }

        var affectedGroupIds = await _duplicateMutationSupportService.CollectAffectedGroupIdsAsync([postId], cancellationToken);

        // Commit zone: do not allow cooperative cancellation once mutation begins.
        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);
        
        var deleteResult = _duplicateMutationSupportService.DeletePostFromDiskAndDb(postToDelete);
        if (!deleteResult.IsSuccess)
        {
            return deleteResult;
        }

        await _context.SaveChangesAsync(CancellationToken.None);
        await _duplicateMutationSupportService.ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        
        return Result.Success();
    }

    public async Task<Result<ResolveSameFolderResponseDto>> ResolveSameFolderGroupAsync(
        ResolveSameFolderGroupRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request.ParentDuplicateGroupId <= 0 || request.LibraryId <= 0)
        {
            return Result<ResolveSameFolderResponseDto>.Failure(OperationError.InvalidInput, "Invalid request payload.");
        }

        var partitionResult = await _duplicateMutationSupportService.LoadSameFolderPartitionAsync(
            request.ParentDuplicateGroupId,
            request.LibraryId,
            request.FolderPath,
            cancellationToken);
        if (!partitionResult.IsSuccess)
        {
            return Result<ResolveSameFolderResponseDto>.Failure(partitionResult.Error ?? OperationError.InvalidInput, partitionResult.Message ?? "Request failed.");
        }

        var resolveResult = await ResolveSameFolderPartitionAsync(partitionResult.Value!, cancellationToken);
        if (!resolveResult.IsSuccess)
        {
            return resolveResult;
        }

        return Result<ResolveSameFolderResponseDto>.Success(resolveResult.Value!);
    }

    public async Task<Result<ResolveSameFolderResponseDto>> ResolveAllSameFolderAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _duplicateQueryService.GetSameFolderDuplicateGroupsAsync(cancellationToken);
        if (groups.Count == 0)
        {
            return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto());
        }

        var summary = new ResolveSameFolderResponseDto();
        foreach (var group in groups)
        {
            var partitionResult = await _duplicateMutationSupportService.LoadSameFolderPartitionAsync(
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

                return Result<ResolveSameFolderResponseDto>.Failure(partitionResult.Error ?? OperationError.InvalidInput, partitionResult.Message ?? "Request failed.");
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
    private async Task<Result<ResolveSameFolderResponseDto>> ResolveSameFolderPartitionAsync(
        DuplicatePartition partitionContext,
        CancellationToken cancellationToken)
    {
        if (partitionContext.Posts.Count < 2)
        {
            return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto
            {
                SkippedGroups = 1
            });
        }

        var keepPostId = _duplicateMutationSupportService.SelectBestQualityPostId(partitionContext.Posts);
        var postIdsToDelete = partitionContext.Posts
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

        var affectedGroupIds = await _duplicateMutationSupportService.CollectAffectedGroupIdsAsync(postIdsToDelete, cancellationToken);

        // Commit zone: do not allow cooperative cancellation once mutation begins.
        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);
        var deleted = 0;
        foreach (var post in partitionContext.Posts.Where(p => p.Id != keepPostId))
        {
            var deleteResult = _duplicateMutationSupportService.DeletePostFromDiskAndDb(post);
            if (!deleteResult.IsSuccess)
            {
                return Result<ResolveSameFolderResponseDto>.Failure(deleteResult.Error ?? OperationError.InvalidInput, deleteResult.Message ?? "Request failed.");
            }

            deleted++;
        }

        await _context.SaveChangesAsync(CancellationToken.None);
        await _duplicateMutationSupportService.ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result<ResolveSameFolderResponseDto>.Success(new ResolveSameFolderResponseDto
        {
            ResolvedGroups = 1,
            DeletedPosts = deleted,
        });
    }

}
