using System;
using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Core.Entities;

public class Tag
{
    public int Id { get; set; }
    
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    
    public int? TagCategoryId { get; set; }
    public TagCategory? TagCategory { get; set; }
    
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
}
