using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

/// <summary>
/// One piece of content. Every file with the same content hash belongs to the same post,
/// so content properties live here and <see cref="PostFile"/> only says where copies are.
/// </summary>
public class Post
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? PdqHash256 { get; set; }

    /// <summary>When this post was first imported into Damebooru.</summary>
    public DateTime ImportDate { get; set; }

    /// <summary>
    /// Earliest modified date of the post's files, kept in sync by DB triggers. Used for sorting.
    /// </summary>
    public DateTime FileModifiedDate { get; set; }

    public bool IsFavorite { get; set; }

    public ICollection<PostFile> PostFiles { get; set; } = new List<PostFile>();
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
    public ICollection<PostSource> Sources { get; set; } = new List<PostSource>();
    public ICollection<DuplicateGroupEntry> DuplicateGroupEntries { get; set; } = new List<DuplicateGroupEntry>();
}
