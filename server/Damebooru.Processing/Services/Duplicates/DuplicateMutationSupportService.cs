using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public sealed record DuplicatePartition(List<Post> Posts);

public class DuplicateMutationSupportService
{
    private readonly DamebooruDbContext _context;

    public DuplicateMutationSupportService(DamebooruDbContext context)
    {
        _context = context;
    }

    public Result DeletePostFromDiskAndDb(Post post)
    {
        var fullPath = Path.GetFullPath(Path.Combine(post.Library.Path, post.RelativePath));
        var libraryRoot = Path.GetFullPath(post.Library.Path + Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
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

    public async Task ResolveGroupKeepingPostAsync(DuplicateGroup group, int keepPostId, bool saveChanges = true)
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

    public async Task<Result<DuplicatePartition>> LoadSameFolderPartitionAsync(
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
            return Result<DuplicatePartition>.Failure(OperationError.NotFound, "Duplicate group not found.");
        }

        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        var partitionPosts = group.Entries
            .Select(e => e.Post)
            .Where(p => p.LibraryId == libraryId && GetParentFolderPath(p.RelativePath) == normalizedFolderPath)
            .ToList();

        if (partitionPosts.Count < 2)
        {
            return Result<DuplicatePartition>.Failure(OperationError.InvalidInput, "Same-folder partition no longer has at least two posts.");
        }

        return Result<DuplicatePartition>.Success(new DuplicatePartition(partitionPosts));
    }

    public int SelectBestQualityPostId(IEnumerable<Post> posts)
    {
        return posts
            .OrderByDescending(p => (long)p.Width * p.Height)
            .ThenByDescending(p => p.SizeBytes)
            .ThenByDescending(p => p.FileModifiedDate)
            .ThenByDescending(p => p.Id)
            .Select(p => p.Id)
            .First();
    }

    public bool HasSameFolderPeer(IEnumerable<Post> posts, Post target)
    {
        var targetFolderPath = GetParentFolderPath(target.RelativePath);
        return posts.Any(post =>
            post.Id != target.Id
            && post.LibraryId == target.LibraryId
            && GetParentFolderPath(post.RelativePath) == targetFolderPath);
    }

    public async Task<List<int>> CollectAffectedGroupIdsAsync(IReadOnlyCollection<int> postIds, CancellationToken cancellationToken)
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

    public async Task ReconcileDuplicateGroupsAsync(IReadOnlyCollection<int> groupIds, CancellationToken cancellationToken)
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

    private static string GetParentFolderPath(string relativePath)
    {
        var normalizedPath = NormalizePath(relativePath);
        var slashIndex = normalizedPath.LastIndexOf('/');
        return slashIndex < 0 ? string.Empty : normalizedPath[..slashIndex];
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }

        return NormalizePath(folderPath);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        normalized = normalized.Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }
}
