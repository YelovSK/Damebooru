using Bakabooru.Core.DTOs;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class TagService
{
    private readonly BakabooruDbContext _context;

    public TagService(BakabooruDbContext context)
    {
        _context = context;
    }

    public async Task<TagListDto> GetTagsAsync(string? query = null, int page = 1, int pageSize = 100, CancellationToken cancellationToken = default)
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

        return new TagListDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<Result<TagDto>> CreateTagAsync(CreateTagDto dto)
    {
        var name = SanitizeTagName(dto.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<TagDto>.Failure(OperationError.InvalidInput, "Tag name cannot be empty.");
        }

        var exists = await _context.Tags.AnyAsync(t => t.Name == name);
        if (exists)
        {
            return Result<TagDto>.Failure(OperationError.Conflict, "Tag already exists.");
        }

        if (dto.CategoryId.HasValue)
        {
            var categoryExists = await _context.TagCategories.AnyAsync(c => c.Id == dto.CategoryId.Value);
            if (!categoryExists)
            {
                return Result<TagDto>.Failure(OperationError.InvalidInput, "Category not found.");
            }
        }

        var tag = new Tag
        {
            Name = name,
            TagCategoryId = dto.CategoryId
        };

        _context.Tags.Add(tag);
        await _context.SaveChangesAsync();

        return Result<TagDto>.Success(new TagDto
        {
            Id = tag.Id,
            Name = tag.Name,
            CategoryId = tag.TagCategoryId,
            Usages = 0
        });
    }

    public async Task<Result<TagDto>> UpdateTagAsync(int id, UpdateTagDto dto)
    {
        var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Id == id);
        if (tag == null)
        {
            return Result<TagDto>.Failure(OperationError.NotFound, "Tag not found.");
        }

        var name = SanitizeTagName(dto.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<TagDto>.Failure(OperationError.InvalidInput, "Tag name cannot be empty.");
        }

        var duplicate = await _context.Tags.AnyAsync(t => t.Id != id && t.Name == name);
        if (duplicate)
        {
            return Result<TagDto>.Failure(OperationError.Conflict, "Another tag with this name already exists.");
        }

        if (dto.CategoryId.HasValue)
        {
            var categoryExists = await _context.TagCategories.AnyAsync(c => c.Id == dto.CategoryId.Value);
            if (!categoryExists)
            {
                return Result<TagDto>.Failure(OperationError.InvalidInput, "Category not found.");
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

        return Result<TagDto>.Success(updated);
    }

    public async Task<Result> MergeTagAsync(int sourceTagId, int targetTagId)
    {
        if (sourceTagId == targetTagId)
        {
            return Result.Failure(OperationError.InvalidInput, "Cannot merge a tag into itself.");
        }

        var source = await _context.Tags.FirstOrDefaultAsync(t => t.Id == sourceTagId);
        if (source == null)
        {
            return Result.Failure(OperationError.NotFound, "Source tag not found.");
        }

        var target = await _context.Tags.FirstOrDefaultAsync(t => t.Id == targetTagId);
        if (target == null)
        {
            return Result.Failure(OperationError.NotFound, "Target tag not found.");
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

        // Transfer category from source to target if target has none
        if (target.TagCategoryId == null && source.TagCategoryId != null)
        {
            target.TagCategoryId = source.TagCategoryId;
        }

        _context.Tags.Remove(source);
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result> DeleteTagAsync(int id)
    {
        var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Id == id);
        if (tag == null)
        {
            return Result.Failure(OperationError.NotFound, "Tag not found.");
        }

        var links = await _context.PostTags
            .Where(pt => pt.TagId == id)
            .ToListAsync();

        _context.PostTags.RemoveRange(links);
        _context.Tags.Remove(tag);
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    private static string NormalizeSearchTerm(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return query
            .Replace("sort:usages", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('*', ' ')
            .Trim();
    }

    internal static string SanitizeTagName(string name)
    {
        var sanitized = name.Trim().ToLowerInvariant();
        sanitized = sanitized.Replace(':', '_');
        // Collapse multiple consecutive underscores
        while (sanitized.Contains("__"))
        {
            sanitized = sanitized.Replace("__", "_");
        }
        return sanitized.Trim('_');
    }
}

