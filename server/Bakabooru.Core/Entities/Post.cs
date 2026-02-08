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
    public string Md5Hash { get; set; } = string.Empty;
    
    public ulong? PerceptualHash { get; set; }
    
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    
    public DateTime ImportDate { get; set; } = DateTime.UtcNow;
    
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
}
