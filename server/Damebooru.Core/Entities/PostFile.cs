using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

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
    public string ContentHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? FileIdentityDevice { get; set; }

    [MaxLength(128)]
    public string? FileIdentityValue { get; set; }

    [MaxLength(64)]
    public string? PdqHash256 { get; set; }

    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    public DateTime FileModifiedDate { get; set; }
}
