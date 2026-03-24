namespace Damebooru.Core.Entities;

public class PostAutoTagScan
{
    public int Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public string ContentHash { get; set; } = string.Empty;
    public string? Md5Hash { get; set; }
    public decimal SauceNaoMinimumSimilarity { get; set; }
    public AutoTagScanStatus Status { get; set; } = AutoTagScanStatus.Pending;
    public DateTime? LastStartedAtUtc { get; set; }
    public DateTime? LastCompletedAtUtc { get; set; }
    public string? LastError { get; set; }

    public ICollection<PostAutoTagScanStep> Steps { get; set; } = new List<PostAutoTagScanStep>();
    public ICollection<PostAutoTagScanCandidate> Candidates { get; set; } = new List<PostAutoTagScanCandidate>();
    public ICollection<PostAutoTagScanSource> Sources { get; set; } = new List<PostAutoTagScanSource>();
    public ICollection<PostAutoTagScanTag> Tags { get; set; } = new List<PostAutoTagScanTag>();
}
