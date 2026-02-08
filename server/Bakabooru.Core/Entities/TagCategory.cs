using System;

namespace Bakabooru.Core.Entities;

public class TagCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#FFFFFF"; // Hex color
    public int Order { get; set; }
    
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
}
