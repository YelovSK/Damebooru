using Bakabooru.Core.DTOs;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Paths;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class DuplicateService
{
    private readonly BakabooruDbContext _context;

    public DuplicateService(BakabooruDbContext context)
    {
        _context = context;
    }

    public Task<List<DuplicateGroupDto>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        return _context.DuplicateGroups
            .Where(g => !g.IsResolved)
            .OrderByDescending(g => g.SimilarityPercent ?? 100)
            .ThenByDescending(g => g.DetectedDate)
            .Select(g => new DuplicateGroupDto
            {
                Id = g.Id,
                Type = g.Type,
                SimilarityPercent = g.SimilarityPercent,
                DetectedDate = g.DetectedDate,
                Posts = g.Entries.Select(e => new DuplicatePostDto
                {
                    Id = e.Post.Id,
                    LibraryId = e.Post.LibraryId,
                    RelativePath = e.Post.RelativePath,
                    ContentHash = e.Post.ContentHash,
                    Width = e.Post.Width,
                    Height = e.Post.Height,
                    ContentType = e.Post.ContentType,
                    SizeBytes = e.Post.SizeBytes,
                    ImportDate = e.Post.ImportDate,
                    ThumbnailUrl = MediaPaths.GetThumbnailUrl(e.Post.ContentHash),
                    ContentUrl = MediaPaths.GetPostContentUrl(e.Post.Id)
                }).ToList()
            }).ToListAsync(cancellationToken);
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
            var keepPostId = group.Entries
                .OrderBy(e => e.Post.ImportDate)
                .First().PostId;

            await ResolveGroupKeepingPostAsync(group, keepPostId, saveChanges: false);
            resolved++;
        }

        await _context.SaveChangesAsync();
        return new ResolveAllExactResponseDto { Resolved = resolved };
    }

    private async Task ResolveGroupKeepingPostAsync(DuplicateGroup group, int keepPostId, bool saveChanges = true)
    {
        var keptEntry = group.Entries.First(e => e.PostId == keepPostId);
        var keptPost = keptEntry.Post;
        var removedEntries = group.Entries.Where(e => e.PostId != keepPostId).ToList();

        // Collect existing tag IDs and source URLs on the survivor
        var existingTagIds = new HashSet<int>(keptPost.PostTags.Select(pt => pt.TagId));
        var existingSourceUrls = new HashSet<string>(
            keptPost.Sources.Select(s => s.Url),
            StringComparer.OrdinalIgnoreCase);
        var maxSourceOrder = keptPost.Sources.Count > 0
            ? keptPost.Sources.Max(s => s.Order)
            : -1;

        foreach (var entry in removedEntries)
        {
            var post = entry.Post;

            // Merge tags from loser into survivor
            foreach (var pt in post.PostTags)
            {
                if (existingTagIds.Add(pt.TagId))
                {
                    _context.PostTags.Add(new PostTag
                    {
                        PostId = keepPostId,
                        TagId = pt.TagId,
                    });
                }
            }

            // Merge sources from loser into survivor
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

        group.IsResolved = true;

        if (saveChanges)
        {
            await _context.SaveChangesAsync();
        }
    }
}

