using System;
using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

public class Tag
{
    public int Id { get; set; }
    
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    
    public TagCategoryKind Category { get; set; } = TagCategoryKind.General;

    public int PostCount { get; set; }
    
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();

    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var normalized = name.Trim().ToLowerInvariant();
        normalized = normalized.Replace(':', '_');
        while (normalized.Contains("__"))
        {
            normalized = normalized.Replace("__", "_");
        }

        return normalized.Trim('_');
    }
}
