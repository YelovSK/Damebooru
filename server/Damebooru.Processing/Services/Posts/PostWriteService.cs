using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class PostWriteService
{
    private readonly DamebooruDbContext _context;

    public PostWriteService(DamebooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result> AddTagAsync(int postId, string tagName)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists) return Result.Failure(OperationError.NotFound, "Post not found");

        tagName = Tag.NormalizeName(tagName);
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

        tagName = Tag.NormalizeName(tagName);
        if (string.IsNullOrEmpty(tagName)) return Result.Failure(OperationError.InvalidInput, "Tag name cannot be empty");

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

        var normalizedSources = NormalizeSources(sources);

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

        if (metadata.TagsWithSources == null && metadata.Sources == null)
        {
            return Result.Success();
        }

        if (metadata.TagsWithSources is { } tagsWithSources)
        {
            var invalidTag = tagsWithSources.FirstOrDefault(tag => !Enum.IsDefined(tag.Source));
            if (invalidTag != null)
            {
                return Result.Failure(OperationError.InvalidInput, $"Invalid post tag source '{invalidTag.Source}'.");
            }

            var desiredTagLinks = tagsWithSources
                .Select(tag => (Name: Tag.NormalizeName(tag.Name), tag.Source))
                .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
                .Distinct()
                .ToList();
            var desiredTagKeys = desiredTagLinks.ToHashSet();

            var existingPostTags = await _context.PostTags
                .Where(pt => pt.PostId == postId)
                .Include(pt => pt.Tag)
                .ToListAsync();

            var toRemove = existingPostTags
                .Where(pt => !desiredTagKeys.Contains((pt.Tag.Name, pt.Source)))
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

        if (metadata.Sources is { } sources)
        {
            var normalizedSources = NormalizeSources(sources);
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

    private static List<string> NormalizeSources(IEnumerable<string> sources)
        => sources
            .Select(source => source.Trim())
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
