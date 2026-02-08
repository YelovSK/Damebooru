using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Server.DTOs;

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
}

public class TagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryColor { get; set; }
}
