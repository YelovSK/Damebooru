using Bakabooru.Core.DTOs;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class PostWriteService
{
    private readonly BakabooruDbContext _context;

    public PostWriteService(BakabooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result> AddTagAsync(int postId, string tagName)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists) return Result.Failure(OperationError.NotFound, "Post not found");

        tagName = tagName.Trim();
        if (string.IsNullOrEmpty(tagName)) return Result.Failure(OperationError.InvalidInput, "Tag name cannot be empty");

        var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Name == tagName);
        if (tag == null)
        {
            tag = new Tag { Name = tagName };
            _context.Tags.Add(tag);
            await _context.SaveChangesAsync();
        }

        var alreadyAssigned = await _context.PostTags.AnyAsync(pt =>
            pt.PostId == postId
            && pt.TagId == tag.Id
            && pt.Source == PostTagSource.Manual);
        if (alreadyAssigned)
        {
            return Result.Failure(OperationError.Conflict, "Tag already assigned");
        }

        _context.PostTags.Add(new PostTag
        {
            PostId = postId,
            TagId = tag.Id,
            Source = PostTagSource.Manual
        });
        await _context.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> RemoveTagAsync(int postId, string tagName)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists) return Result.Failure(OperationError.NotFound, "Post not found");

        var postTag = await _context.PostTags
            .Where(pt =>
                pt.PostId == postId
                && pt.Source == PostTagSource.Manual
                && pt.Tag.Name == tagName)
            .FirstOrDefaultAsync();
        if (postTag == null) return Result.Failure(OperationError.NotFound, "Tag not found on post");

        _context.PostTags.Remove(postTag);
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result> FavoriteAsync(int postId)
    {
        var post = await _context.Posts.FindAsync(postId);
        if (post == null)
        {
            return Result.Failure(OperationError.NotFound, "Post not found");
        }

        if (!post.IsFavorite)
        {
            post.IsFavorite = true;
            await _context.SaveChangesAsync();
        }

        return Result.Success();
    }

    public async Task<Result> UnfavoriteAsync(int postId)
    {
        var post = await _context.Posts.FindAsync(postId);
        if (post == null)
        {
            return Result.Failure(OperationError.NotFound, "Post not found");
        }

        if (post.IsFavorite)
        {
            post.IsFavorite = false;
            await _context.SaveChangesAsync();
        }

        return Result.Success();
    }

    public async Task<Result> SetSourcesAsync(int postId, IEnumerable<string> sources)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists)
        {
            return Result.Failure(OperationError.NotFound, "Post not found");
        }

        var normalizedSources = sources
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _context.PostSources
            .Where(ps => ps.PostId == postId)
            .ExecuteDeleteAsync();

        if (normalizedSources.Count > 0)
        {
            for (var i = 0; i < normalizedSources.Count; i++)
            {
                _context.PostSources.Add(new PostSource
                {
                    PostId = postId,
                    Url = normalizedSources[i],
                    Order = i
                });
            }

            await _context.SaveChangesAsync();
        }

        return Result.Success();
    }

    public async Task<Result> UpdateMetadataAsync(int postId, UpdatePostMetadataDto? metadata)
    {
        if (metadata == null)
        {
            return Result.Failure(OperationError.InvalidInput, "Request body is required");
        }

        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists)
        {
            return Result.Failure(OperationError.NotFound, "Post not found");
        }

        var updateTags = metadata.TagsWithSources != null;
        var updateSources = metadata.Sources != null;
        if (!updateTags && !updateSources)
        {
            return Result.Success();
        }

        if (updateTags)
        {
            var desiredTagLinks = new List<(string Name, PostTagSource Source)>();
            var desiredTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var requestedTag in metadata.TagsWithSources!)
            {
                var normalizedTagName = requestedTag.Name?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedTagName))
                {
                    continue;
                }

                if (!Enum.IsDefined(requestedTag.Source))
                {
                    return Result.Failure(OperationError.InvalidInput, $"Invalid post tag source '{requestedTag.Source}'.");
                }

                var key = BuildTagKey(normalizedTagName, requestedTag.Source);
                if (!desiredTagKeys.Add(key))
                {
                    continue;
                }

                desiredTagLinks.Add((normalizedTagName, requestedTag.Source));
            }

            var existingPostTags = await _context.PostTags
                .Where(pt => pt.PostId == postId)
                .Include(pt => pt.Tag)
                .ToListAsync();

            var toRemove = existingPostTags
                .Where(pt => !desiredTagKeys.Contains(BuildTagKey(pt.Tag.Name, pt.Source)))
                .ToList();
            if (toRemove.Count > 0)
            {
                _context.PostTags.RemoveRange(toRemove);
            }

            var existingTagLinks = existingPostTags
                .Select(pt => (pt.Tag.Name, pt.Source))
                .ToHashSet();
            var missingTagLinks = desiredTagLinks
                .Where(link => !existingTagLinks.Contains(link))
                .ToList();

            if (missingTagLinks.Count > 0)
            {
                var missingTagNames = missingTagLinks
                    .Select(link => link.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var existingTags = await _context.Tags
                    .Where(t => missingTagNames.Contains(t.Name))
                    .ToListAsync();
                var tagsByName = existingTags
                    .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var tagName in missingTagNames)
                {
                    if (!tagsByName.TryGetValue(tagName, out var tag))
                    {
                        tag = new Tag { Name = tagName };
                        _context.Tags.Add(tag);
                        tagsByName[tagName] = tag;
                    }
                }

                foreach (var missingTagLink in missingTagLinks)
                {
                    _context.PostTags.Add(new PostTag
                    {
                        PostId = postId,
                        Tag = tagsByName[missingTagLink.Name],
                        Source = missingTagLink.Source
                    });
                }
            }
        }

        if (updateSources)
        {
            var normalizedSources = new List<string>();
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in metadata.Sources!)
            {
                var normalized = source?.Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (seenSources.Add(normalized))
                {
                    normalizedSources.Add(normalized);
                }
            }

            var existingSources = await _context.PostSources
                .Where(ps => ps.PostId == postId)
                .ToListAsync();
            if (existingSources.Count > 0)
            {
                _context.PostSources.RemoveRange(existingSources);
            }

            for (var i = 0; i < normalizedSources.Count; i++)
            {
                _context.PostSources.Add(new PostSource
                {
                    PostId = postId,
                    Url = normalizedSources[i],
                    Order = i
                });
            }
        }

        await _context.SaveChangesAsync();
        return Result.Success();
    }

    private static string BuildTagKey(string tagName, PostTagSource source)
    {
        return $"{source}|{tagName}";
    }
}
