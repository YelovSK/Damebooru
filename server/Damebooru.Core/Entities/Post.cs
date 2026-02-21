using System;
using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

public class Post
{
    public int Id { get; set; }

    public int LibraryId { get; set; }
    public Library Library { get; set; } = null!;

    [MaxLength(500)]
    public string RelativePath { get; set; } = string.Empty;

    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? FileIdentityDevice { get; set; }

    [MaxLength(128)]
    public string? FileIdentityValue { get; set; }

    /// <summary>Perceptual difference hash (dHash).</summary>
    public ulong? PerceptualHash { get; set; }

    /// <summary>Perceptual frequency hash (pHash).</summary>
    public ulong? PerceptualHashP { get; set; }

    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>When this post was first imported into Damebooru.</summary>
    public DateTime ImportDate { get; set; }

    /// <summary>File's last modified time at time of scan, for sorting and change detection.</summary>
    public DateTime FileModifiedDate { get; set; }

    public bool IsFavorite { get; set; }

    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
    public ICollection<PostSource> Sources { get; set; } = new List<PostSource>();
    public ICollection<DuplicateGroupEntry> DuplicateGroupEntries { get; set; } = new List<DuplicateGroupEntry>();
}
