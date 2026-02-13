using System;
using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Core.Entities;

public class Post
{
    public int Id { get; set; }
    
    public int LibraryId { get; set; }
    public Library Library { get; set; } = null!;
    
    [MaxLength(500)]
    public string RelativePath { get; set; } = string.Empty;
    
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;
    
    public ulong? PerceptualHash { get; set; }
    
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    
    public DateTime ImportDate { get; set; }

    /// <summary>File's last modified time at time of scan, for change detection.</summary>
    public DateTime? FileModifiedDate { get; set; }

    public bool IsFavorite { get; set; }
    
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
    public ICollection<PostSource> Sources { get; set; } = new List<PostSource>();
}
