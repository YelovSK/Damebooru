using Bakabooru.Core.Entities;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public enum AddTagError
{
    None,
    PostNotFound,
    EmptyTagName,
    TagAlreadyAssigned
}

public enum RemoveTagError
{
    None,
    PostNotFound,
    TagNotFoundOnPost
}

public readonly record struct AddTagResult(AddTagError Error);
public readonly record struct RemoveTagResult(RemoveTagError Error);

public class PostWriteService
{
    private readonly BakabooruDbContext _context;

    public PostWriteService(BakabooruDbContext context)
    {
        _context = context;
    }

    public async Task<AddTagResult> AddTagAsync(int postId, string tagName)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists) return new AddTagResult(AddTagError.PostNotFound);

        tagName = tagName.Trim();
        if (string.IsNullOrEmpty(tagName)) return new AddTagResult(AddTagError.EmptyTagName);

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
            return new AddTagResult(AddTagError.TagAlreadyAssigned);
        }

        _context.PostTags.Add(new PostTag { PostId = postId, TagId = tag.Id });
        await _context.SaveChangesAsync();

        return new AddTagResult(AddTagError.None);
    }

    public async Task<RemoveTagResult> RemoveTagAsync(int postId, string tagName)
    {
        var postExists = await _context.Posts.AnyAsync(p => p.Id == postId);
        if (!postExists) return new RemoveTagResult(RemoveTagError.PostNotFound);

        var postTag = await _context.PostTags
            .Where(pt => pt.PostId == postId && pt.Tag.Name == tagName)
            .FirstOrDefaultAsync();
        if (postTag == null) return new RemoveTagResult(RemoveTagError.TagNotFoundOnPost);

        _context.PostTags.Remove(postTag);
        await _context.SaveChangesAsync();
        return new RemoveTagResult(RemoveTagError.None);
    }
}
