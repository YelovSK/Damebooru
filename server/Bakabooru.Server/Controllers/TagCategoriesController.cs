using Bakabooru.Core.Entities;
using Bakabooru.Core.DTOs;
using Bakabooru.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TagCategoriesController : ControllerBase
{
    private readonly BakabooruDbContext _context;

    public TagCategoriesController(BakabooruDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IEnumerable<TagCategoryDto>> GetCategories(CancellationToken cancellationToken = default)
    {
        return await _context.TagCategories
            .OrderBy(c => c.Order)
            .Select(c => new TagCategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Color = c.Color,
                Order = c.Order,
                TagCount = c.Tags.Count
            })
            .ToListAsync(cancellationToken);
    }

    [HttpPost]
    public async Task<ActionResult<TagCategoryDto>> CreateCategory(CreateTagCategoryDto dto)
    {
        var category = new TagCategory
        {
            Name = dto.Name,
            Color = dto.Color,
            Order = dto.Order
        };

        _context.TagCategories.Add(category);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetCategories), new { id = category.Id }, new TagCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Color = category.Color,
            Order = category.Order,
            TagCount = 0
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<TagCategoryDto>> UpdateCategory(int id, [FromBody] UpdateTagCategoryDto dto)
    {
        var category = await _context.TagCategories
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
        {
            return NotFound();
        }

        category.Name = dto.Name.Trim();
        category.Color = dto.Color;
        category.Order = dto.Order;
        await _context.SaveChangesAsync();

        var tagCount = await _context.Tags.CountAsync(t => t.TagCategoryId == category.Id);

        return Ok(new TagCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Color = category.Color,
            Order = category.Order,
            TagCount = tagCount
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        var category = await _context.TagCategories
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
        {
            return NotFound();
        }

        var tags = await _context.Tags
            .Where(t => t.TagCategoryId == category.Id)
            .ToListAsync();

        foreach (var tag in tags)
        {
            tag.TagCategoryId = null;
        }

        _context.TagCategories.Remove(category);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
