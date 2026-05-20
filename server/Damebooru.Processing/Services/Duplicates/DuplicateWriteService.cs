using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Paths;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Duplicates;

public class DuplicateWriteService
{
    private readonly DamebooruDbContext _context;
    private readonly FolderTaggingService _folderTaggingService;

    public DuplicateWriteService(DamebooruDbContext context, FolderTaggingService folderTaggingService)
    {
        _context = context;
        _folderTaggingService = folderTaggingService;
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
                    .ThenInclude(p => p.PostFiles)
                        .ThenInclude(pf => pf.Library)
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
                    .ThenInclude(p => p.PostFiles)
                        .ThenInclude(pf => pf.Library)
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

    public async Task<Result> ExcludeExactDuplicateFileAsync(int postFileId, CancellationToken cancellationToken = default)
    {
        var postFile = await _context.PostFiles
            .Include(pf => pf.Library)
            .Include(pf => pf.Post)
                .ThenInclude(p => p.PostFiles)
            .FirstOrDefaultAsync(pf => pf.Id == postFileId, cancellationToken);

        if (postFile == null)
        {
            return Result.Failure(OperationError.NotFound, "Duplicate file not found.");
        }

        if (postFile.Post.PostFiles.Count <= 1)
        {
            return Result.Failure(OperationError.InvalidInput, "Cannot exclude the last remaining file on a post.");
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);

        var alreadyExcluded = await _context.ExcludedFiles.AnyAsync(
            e => e.LibraryId == postFile.LibraryId && e.RelativePath == postFile.RelativePath,
            cancellationToken);

        if (!alreadyExcluded)
        {
            _context.ExcludedFiles.Add(new ExcludedFile
            {
                LibraryId = postFile.LibraryId,
                RelativePath = postFile.RelativePath,
                ContentHash = postFile.ContentHash,
                ExcludedDate = DateTime.UtcNow,
                Reason = "duplicate_resolution"
            });
        }

        var affectedPostId = postFile.PostId;
        _context.PostFiles.Remove(postFile);
        await _context.SaveChangesAsync(CancellationToken.None);
        await _folderTaggingService.SyncPostFolderTagsAsync(_context, [affectedPostId], CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result.Success();
    }

    public async Task<Result> DeleteExactDuplicateFileAsync(int postFileId, CancellationToken cancellationToken = default)
    {
        var postFile = await _context.PostFiles
            .Include(pf => pf.Library)
            .Include(pf => pf.Post)
                .ThenInclude(p => p.PostFiles)
            .FirstOrDefaultAsync(pf => pf.Id == postFileId, cancellationToken);

        if (postFile == null)
        {
            return Result.Failure(OperationError.NotFound, "Duplicate file not found.");
        }

        if (!SafeSubpathResolver.TryResolve(postFile.Library.Path, postFile.RelativePath, out var fullPath))
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

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);
        var affectedPostId = postFile.PostId;
        var affectedGroupIds = await CollectAffectedGroupIdsAsync([affectedPostId], cancellationToken);

        var excluded = _context.ExcludedFiles
            .Where(e => e.LibraryId == postFile.LibraryId && e.RelativePath == postFile.RelativePath);
        _context.ExcludedFiles.RemoveRange(excluded);
        _context.PostFiles.Remove(postFile);
        await _context.SaveChangesAsync(CancellationToken.None);

        var hasRemainingFiles = await _context.PostFiles.AnyAsync(pf => pf.PostId == affectedPostId, CancellationToken.None);
        if (!hasRemainingFiles)
        {
            var post = await _context.Posts.FindAsync([affectedPostId], CancellationToken.None);
            if (post != null)
            {
                _context.Posts.Remove(post);
                await _context.SaveChangesAsync(CancellationToken.None);
            }
        }
        else
        {
            await _folderTaggingService.SyncPostFolderTagsAsync(_context, [affectedPostId], CancellationToken.None);
            await _context.SaveChangesAsync(CancellationToken.None);
        }

        await ReconcileDuplicateGroupsAsync(affectedGroupIds, CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);
        return Result.Success();
    }

    public async Task<Result> ExcludeDuplicatePostAsync(int groupId, int postId, CancellationToken cancellationToken = default)
    {
        var group = await _context.DuplicateGroups
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.PostFiles)
                        .ThenInclude(pf => pf.Library)
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
        if (postToExclude.PostFiles.Count == 0)
        {
            return Result.Failure(OperationError.InvalidInput, "Post has no files.");
        }

        var affectedGroupIds = await CollectAffectedGroupIdsAsync([postId], cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);

        await AddExclusionsForPostAsync(postToExclude, cancellationToken);
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
                    .ThenInclude(p => p.PostFiles)
                        .ThenInclude(pf => pf.Library)
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
        var filesToDelete = postToDelete.PostFiles.ToList();
        if (filesToDelete.Count == 0)
        {
            return Result.Failure(OperationError.InvalidInput, "Post has no files.");
        }

        var resolvedFiles = new List<(PostFile File, string FullPath)>();
        foreach (var file in filesToDelete)
        {
            if (!SafeSubpathResolver.TryResolve(file.Library.Path, file.RelativePath, out var fullPath))
            {
                return Result.Failure(OperationError.InvalidInput, "Invalid file path.");
            }

            resolvedFiles.Add((file, fullPath));
        }

        try
        {
            foreach (var (_, fullPath) in resolvedFiles)
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(OperationError.Conflict, $"Failed to delete file from disk: {ex.Message}");
        }

        var affectedGroupIds = await CollectAffectedGroupIdsAsync([postId], cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(CancellationToken.None);

        foreach (var file in filesToDelete)
        {
            var excluded = _context.ExcludedFiles
                .Where(e => e.LibraryId == file.LibraryId && e.RelativePath == file.RelativePath);
            _context.ExcludedFiles.RemoveRange(excluded);
        }

        _context.Posts.Remove(postToDelete);
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

            await AddExclusionsForPostAsync(post);
            _context.Posts.Remove(post);
        }

        _context.DuplicateGroups.Remove(group);

        if (saveChanges)
        {
            await _context.SaveChangesAsync();
        }
    }

    private int SelectBestQualityPostId(IEnumerable<Post> posts)
    {
        return posts
            .OrderByDescending(p => (long)GetRepresentativeWidth(p) * GetRepresentativeHeight(p))
            .ThenByDescending(GetRepresentativeSizeBytes)
            .ThenByDescending(GetRepresentativeFileModifiedDate)
            .ThenByDescending(p => p.Id)
            .Select(p => p.Id)
            .First();
    }

    private async Task AddExclusionsForPostAsync(Post post, CancellationToken cancellationToken = default)
    {
        foreach (var file in post.PostFiles)
        {
            var alreadyExcluded = await _context.ExcludedFiles.AnyAsync(
                e => e.LibraryId == file.LibraryId && e.RelativePath == file.RelativePath,
                cancellationToken);

            if (alreadyExcluded)
            {
                continue;
            }

            _context.ExcludedFiles.Add(new ExcludedFile
            {
                LibraryId = file.LibraryId,
                RelativePath = file.RelativePath,
                ContentHash = file.ContentHash,
                ExcludedDate = DateTime.UtcNow,
                Reason = "duplicate_resolution"
            });
        }
    }

    private static PostFile? GetRepresentativeFile(Post post)
        => PostDto.GetRepresentativeFile(post);

    private static int GetRepresentativeWidth(Post post)
        => GetRepresentativeFile(post)?.Width ?? 0;

    private static int GetRepresentativeHeight(Post post)
        => GetRepresentativeFile(post)?.Height ?? 0;

    private static long GetRepresentativeSizeBytes(Post post)
        => GetRepresentativeFile(post)?.SizeBytes ?? 0;

    private static DateTime GetRepresentativeFileModifiedDate(Post post)
        => GetRepresentativeFile(post)?.FileModifiedDate ?? default;

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

}
