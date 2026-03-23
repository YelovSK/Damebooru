namespace Damebooru.Core.Entities;

public class PostAutoTagScanStep
{
    public int Id { get; set; }

    public int ScanId { get; set; }
    public PostAutoTagScan Scan { get; set; } = null!;

    public AutoTagProvider Provider { get; set; }
    public AutoTagScanStepKind Kind { get; set; }
    public AutoTagScanStepStatus Status { get; set; } = AutoTagScanStepStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }
    public string? LastError { get; set; }
    public long? ExternalPostId { get; set; }
}
