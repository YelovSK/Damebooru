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

    private static IQueryable<Post> ApplyTagFilters(IQueryable<Post> query, string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return query;
        }

        var parsedQuery = Bakabooru.Processing.Pipeline.QueryParser.Parse(tags);

        foreach (var tag in parsedQuery.IncludedTags)
        {
            query = query.Where(p => p.PostTags.Any(pt => pt.Tag.Name == tag));
        }

        foreach (var tag in parsedQuery.ExcludedTags)
        {
            query = query.Where(p => !p.PostTags.Any(pt => pt.Tag.Name == tag));
        }

        return query;
    }

    [HttpGet]
    public async Task<ActionResult<PostListDto>> GetPosts(
        [FromQuery] string? tags = null,
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = ApplyTagFilters(_context.Posts.AsQueryable(), tags);

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
                ContentHash = p.ContentHash,
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                ThumbnailUrl = $"/thumbnails/{p.ContentHash}.jpg",
                ContentUrl = $"/api/posts/{p.Id}/content",
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

    [HttpGet("{id}/around")]
    public async Task<ActionResult<PostsAroundDto>> GetPostsAround(int id, [FromQuery] string? tags = null)
    {
        var current = await _context.Posts
            .Where(p => p.Id == id)
            .Select(p => new { p.Id, p.ImportDate })
            .FirstOrDefaultAsync();

        if (current == null)
        {
            return NotFound();
        }

        var query = ApplyTagFilters(_context.Posts.AsQueryable(), tags);

        var prev = await query
            .Where(p => p.Id != current.Id
                        && (p.ImportDate > current.ImportDate
                            || (p.ImportDate == current.ImportDate && p.Id > current.Id)))
            .OrderBy(p => p.ImportDate)
            .ThenBy(p => p.Id)
            .Select(p => new MicroPostDto
            {
                Id = p.Id,
                ThumbnailUrl = $"/thumbnails/{p.ContentHash}.jpg"
            })
            .FirstOrDefaultAsync();

        var next = await query
            .Where(p => p.Id != current.Id
                        && (p.ImportDate < current.ImportDate
                            || (p.ImportDate == current.ImportDate && p.Id < current.Id)))
            .OrderByDescending(p => p.ImportDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new MicroPostDto
            {
                Id = p.Id,
                ThumbnailUrl = $"/thumbnails/{p.ContentHash}.jpg"
            })
            .FirstOrDefaultAsync();

        return Ok(new PostsAroundDto
        {
            Prev = prev,
            Next = next
        });
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
            ContentHash = p.ContentHash,
            Width = p.Width,
            Height = p.Height,
            ContentType = p.ContentType,
            ImportDate = p.ImportDate,
            ThumbnailUrl = $"/thumbnails/{p.ContentHash}.jpg",
            ContentUrl = $"/api/posts/{p.Id}/content",
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
    public async Task<IActionResult> AddTag(int id, [FromBody] string tagName)
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
            await _context.SaveChangesAsync();
        }

        // Check if post already has this tag
        if (post.PostTags.Any(pt => pt.TagId == tag.Id))
        {
            return Conflict("Tag already assigned");
        }

        post.PostTags.Add(new PostTag { PostId = post.Id, TagId = tag.Id });
        await _context.SaveChangesAsync();

        return NoContent();
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

    [HttpGet("{id}/content")]
    public async Task<IActionResult> GetPostContent(int id)
    {
        var post = await _context.Posts
            .Include(p => p.Library)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null) return NotFound();

        var fullPath = Path.GetFullPath(Path.Combine(post.Library.Path, post.RelativePath));
        var libraryRoot = Path.GetFullPath(post.Library.Path + Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid file path");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound("File not found on disk");
        }

        var stream = System.IO.File.OpenRead(fullPath);
        return File(stream, post.ContentType, enableRangeProcessing: true);
    }
}
