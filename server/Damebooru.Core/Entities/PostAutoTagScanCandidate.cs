namespace Damebooru.Core.Entities;

public class PostAutoTagScanCandidate
{
    public int Id { get; set; }

    public int ScanId { get; set; }
    public PostAutoTagScan Scan { get; set; } = null!;

    public AutoTagProvider DiscoveryProvider { get; set; }
    public AutoTagProvider Provider { get; set; }
    public long ExternalPostId { get; set; }
    public decimal Similarity { get; set; }
    public string CanonicalUrl { get; set; } = string.Empty;
}
