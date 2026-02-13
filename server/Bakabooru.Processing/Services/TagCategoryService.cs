using Bakabooru.Core.DTOs;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class TagCategoryService
{
    private readonly BakabooruDbContext _context;

    public TagCategoryService(BakabooruDbContext context)
    {
        _context = context;
    }

    public Task<List<TagCategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return _context.TagCategories
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

    public async Task<TagCategoryDto> CreateCategoryAsync(CreateTagCategoryDto dto)
    {
        var category = new TagCategory
        {
            Name = dto.Name,
            Color = dto.Color,
            Order = dto.Order
        };

        _context.TagCategories.Add(category);
        await _context.SaveChangesAsync();

        return new TagCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Color = category.Color,
            Order = category.Order,
            TagCount = 0
        };
    }

    public async Task<Result<TagCategoryDto>> UpdateCategoryAsync(int id, UpdateTagCategoryDto dto)
    {
        var category = await _context.TagCategories
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
        {
            return Result<TagCategoryDto>.Failure(OperationError.NotFound, "Category not found.");
        }

        category.Name = dto.Name.Trim();
        category.Color = dto.Color;
        category.Order = dto.Order;
        await _context.SaveChangesAsync();

        var tagCount = await _context.Tags.CountAsync(t => t.TagCategoryId == category.Id);

        return Result<TagCategoryDto>.Success(new TagCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Color = category.Color,
            Order = category.Order,
            TagCount = tagCount
        });
    }

    public async Task<Result> DeleteCategoryAsync(int id)
    {
        var category = await _context.TagCategories
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
        {
            return Result.Failure(OperationError.NotFound, "Category not found.");
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
        return Result.Success();
    }
}

