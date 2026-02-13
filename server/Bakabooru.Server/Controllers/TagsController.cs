using Bakabooru.Core.Entities;
using Bakabooru.Core.DTOs;
using Bakabooru.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TagsController : ControllerBase
{
    private readonly BakabooruDbContext _context;

    public TagsController(BakabooruDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<TagListDto>> GetTags(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 100;
        if (pageSize > 500) pageSize = 500;

        var q = _context.Tags.AsQueryable();

        var search = NormalizeSearchTerm(query);
        if (!string.IsNullOrWhiteSpace(search))
        {
            q = q.Where(t => t.Name.Contains(search));
        }

        var totalCount = await q.CountAsync(cancellationToken);
        var items = await q
            .OrderByDescending(t => t.PostCount)
            .ThenBy(t => t.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TagDto
            {
                Id = t.Id,
                Name = t.Name,
                CategoryId = t.TagCategoryId,
                CategoryName = t.TagCategory != null ? t.TagCategory.Name : null,
                CategoryColor = t.TagCategory != null ? t.TagCategory.Color : null,
                Usages = t.PostCount
            })
            .ToListAsync(cancellationToken);

        return Ok(new TagListDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpPost]
    public async Task<ActionResult<TagDto>> CreateTag([FromBody] CreateTagDto dto)
    {
        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Tag name cannot be empty.");
        }

        var exists = await _context.Tags.AnyAsync(t => t.Name == name);
        if (exists)
        {
            return Conflict("Tag already exists.");
        }

        if (dto.CategoryId.HasValue)
        {
            var categoryExists = await _context.TagCategories.AnyAsync(c => c.Id == dto.CategoryId.Value);
            if (!categoryExists)
            {
                return BadRequest("Category not found.");
            }
        }

        var tag = new Tag
        {
            Name = name,
            TagCategoryId = dto.CategoryId
        };

        _context.Tags.Add(tag);
        await _context.SaveChangesAsync();

        return Ok(new TagDto
        {
            Id = tag.Id,
            Name = tag.Name,
            CategoryId = tag.TagCategoryId,
            Usages = 0
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<TagDto>> UpdateTag(int id, [FromBody] UpdateTagDto dto)
    {
        var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Id == id);

        if (tag == null)
        {
            return NotFound();
        }

        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Tag name cannot be empty.");
        }

        var duplicate = await _context.Tags.AnyAsync(t => t.Id != id && t.Name == name);
        if (duplicate)
        {
            return Conflict("Another tag with this name already exists.");
        }

        if (dto.CategoryId.HasValue)
        {
            var categoryExists = await _context.TagCategories.AnyAsync(c => c.Id == dto.CategoryId.Value);
            if (!categoryExists)
            {
                return BadRequest("Category not found.");
            }
        }

        tag.Name = name;
        tag.TagCategoryId = dto.CategoryId;
        await _context.SaveChangesAsync();

        var updated = await _context.Tags
            .Where(t => t.Id == id)
            .Select(t => new TagDto
            {
                Id = t.Id,
                Name = t.Name,
                CategoryId = t.TagCategoryId,
                CategoryName = t.TagCategory != null ? t.TagCategory.Name : null,
                CategoryColor = t.TagCategory != null ? t.TagCategory.Color : null,
                Usages = t.PostCount
            })
            .FirstAsync();

        return Ok(updated);
    }

    [HttpPost("{id}/merge")]
    public async Task<IActionResult> MergeTag(int id, [FromBody] MergeTagDto dto)
    {
        if (id == dto.TargetTagId)
        {
            return BadRequest("Cannot merge a tag into itself.");
        }

        var source = await _context.Tags.FirstOrDefaultAsync(t => t.Id == id);
        if (source == null)
        {
            return NotFound("Source tag not found.");
        }

        var target = await _context.Tags.FirstOrDefaultAsync(t => t.Id == dto.TargetTagId);
        if (target == null)
        {
            return NotFound("Target tag not found.");
        }

        var targetPostIds = await _context.PostTags
            .Where(pt => pt.TagId == target.Id)
            .Select(pt => pt.PostId)
            .ToHashSetAsync();

        var sourceLinks = await _context.PostTags
            .Where(pt => pt.TagId == source.Id)
            .ToListAsync();

        foreach (var link in sourceLinks)
        {
            if (!targetPostIds.Contains(link.PostId))
            {
                _context.PostTags.Add(new PostTag
                {
                    PostId = link.PostId,
                    TagId = target.Id
                });
            }
        }

        _context.PostTags.RemoveRange(sourceLinks);
        _context.Tags.Remove(source);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTag(int id)
    {
        var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Id == id);

        if (tag == null)
        {
            return NotFound();
        }

        var links = await _context.PostTags
            .Where(pt => pt.TagId == id)
            .ToListAsync();
        _context.PostTags.RemoveRange(links);
        _context.Tags.Remove(tag);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static string NormalizeSearchTerm(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var clean = query
            .Replace("sort:usages", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('*', ' ')
            .Trim();

        return clean;
    }
}
