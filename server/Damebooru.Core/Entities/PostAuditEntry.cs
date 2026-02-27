using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

public class PostAuditEntry
{
    public long Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public DateTime OccurredAtUtc { get; set; }

    [MaxLength(32)]
    public string Entity { get; set; } = string.Empty;

    [MaxLength(16)]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Field { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
