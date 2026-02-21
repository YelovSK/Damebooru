using System.ComponentModel.DataAnnotations;
using Bakabooru.Core.Entities;

namespace Bakabooru.Core.DTOs;

public class CreateTagCategoryDto
{
    [Required]
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#FFFFFF";
    public int Order { get; set; }
}

public class TagCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int Order { get; set; }
    public int TagCount { get; set; }
}

public class UpdateTagCategoryDto
{
    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#FFFFFF";
    public int Order { get; set; }
}

public class TagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryColor { get; set; }
    public int Usages { get; set; }
    public PostTagSource Source { get; set; }
}

public class CreateTagDto
{
    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
}

public class UpdateTagDto
{
    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
}

public class MergeTagDto
{
    [Required]
    [Range(1, int.MaxValue)]
    public int TargetTagId { get; set; }
}

public class TagListDto
{
    public List<TagDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
