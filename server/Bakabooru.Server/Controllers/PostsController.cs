using Bakabooru.Core.Entities;
using Bakabooru.Data;
using Bakabooru.Server.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PostsController : ControllerBase
{
    private readonly BakabooruDbContext _context;

    public PostsController(BakabooruDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<PostListDto>> GetPosts(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _context.Posts.AsQueryable();

        var totalCount = await query.CountAsync();
        var items = await query
            .Include(p => p.PostTags)
            .ThenInclude(pt => pt.Tag)
            .ThenInclude(t => t.TagCategory)
            .OrderByDescending(p => p.ImportDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = p.LibraryId,
                RelativePath = p.RelativePath,
                Md5Hash = p.Md5Hash,
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                Tags = p.PostTags.Select(pt => new TagDto
                {
                    Id = pt.Tag.Id,
                    Name = pt.Tag.Name,
                    CategoryId = pt.Tag.TagCategoryId,
                    CategoryName = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Name : null,
                    CategoryColor = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Color : null
                }).ToList()
            })
            .ToListAsync();

        return new PostListDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PostDto>> GetPost(int id)
    {
        var p = await _context.Posts
            .Include(p => p.PostTags)
            .ThenInclude(pt => pt.Tag)
            .ThenInclude(t => t.TagCategory)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (p == null) return NotFound();

        return new PostDto
        {
            Id = p.Id,
            LibraryId = p.LibraryId,
            RelativePath = p.RelativePath,
            Md5Hash = p.Md5Hash,
            Width = p.Width,
            Height = p.Height,
            ContentType = p.ContentType,
            ImportDate = p.ImportDate,
            Tags = p.PostTags.Select(pt => new TagDto
            {
                Id = pt.Tag.Id,
                Name = pt.Tag.Name,
                CategoryId = pt.Tag.TagCategoryId,
                CategoryName = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Name : null,
                CategoryColor = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Color : null
            }).ToList()
        };
    }

    [HttpPost("{id}/tags")]
    public async Task<ActionResult<List<TagDto>>> AddTag(int id, [FromBody] string tagName)
    {
        var post = await _context.Posts
            .Include(p => p.PostTags)
            .ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null) return NotFound("Post not found");

        tagName = tagName.Trim();
        if (string.IsNullOrEmpty(tagName)) return BadRequest("Tag name cannot be empty");

        // Check if tag exists
        var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Name == tagName);
        if (tag == null)
        {
            // Create new tag
            tag = new Tag { Name = tagName };
            _context.Tags.Add(tag);
            // Save to get Id
            await _context.SaveChangesAsync();
        }

        // Check if post already has this tag
        if (post.PostTags.Any(pt => pt.TagId == tag.Id))
        {
            return Ok("Tag already assigned");
        }

        post.PostTags.Add(new PostTag { PostId = post.Id, TagId = tag.Id });
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetPost), new { id = post.Id }, null);
    }
    
    [HttpDelete("{id}/tags/{tagName}")]
    public async Task<IActionResult> RemoveTag(int id, string tagName)
    {
        var post = await _context.Posts
            .Include(p => p.PostTags)
            .ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null) return NotFound("Post not found");

        var postTag = post.PostTags.FirstOrDefault(pt => pt.Tag.Name == tagName);
        if (postTag == null) return NotFound("Tag not found on post");

        post.PostTags.Remove(postTag);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
