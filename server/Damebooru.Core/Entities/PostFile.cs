using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

/// <summary>
/// A place on disk where a post's content exists.
/// </summary>
public class PostFile
{
    public int Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public int LibraryId { get; set; }
    public Library Library { get; set; } = null!;

    [MaxLength(500)]
    public string RelativePath { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? FileIdentityDevice { get; set; }

    [MaxLength(128)]
    public string? FileIdentityValue { get; set; }

    public DateTime FileModifiedDate { get; set; }
}
