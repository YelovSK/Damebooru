using Bakabooru.Core.Entities;
using Bakabooru.Data;
using Bakabooru.Server.DTOs;
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
    public async Task<IEnumerable<TagCategoryDto>> GetCategories()
    {
        return await _context.TagCategories
            .OrderBy(c => c.Order)
            .Select(c => new TagCategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Color = c.Color,
                Order = c.Order
            })
            .ToListAsync();
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
            Order = category.Order
        });
    }
}
