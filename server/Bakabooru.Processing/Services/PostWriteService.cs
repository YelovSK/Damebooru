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

        var alreadyAssigned = await _context.PostTags.AnyAsync(pt => pt.PostId == postId && pt.TagId == tag.Id);
        if (alreadyAssigned)
        {
            return Result.Failure(OperationError.Conflict, "Tag already assigned");
        }

        _context.PostTags.Add(new PostTag { PostId = postId, TagId = tag.Id });
        await _context.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> RemoveTagAsync(int postId, string tagName)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists) return Result.Failure(OperationError.NotFound, "Post not found");

        var postTag = await _context.PostTags
            .Where(pt => pt.PostId == postId && pt.Tag.Name == tagName)
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
}
