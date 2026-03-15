using System.ComponentModel.DataAnnotations;
using Damebooru.Core.Entities;

namespace Damebooru.Core.DTOs;

public class TagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TagCategoryKind Category { get; set; }
    public int Usages { get; set; }
    public List<PostTagSource> Sources { get; set; } = [];
}

public class CreateTagDto
{
    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;
    public TagCategoryKind Category { get; set; } = TagCategoryKind.General;
}

public class UpdateTagDto
{
    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;
    public TagCategoryKind Category { get; set; } = TagCategoryKind.General;
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
