using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

public class PostAutoTagScanSource
{
    public int Id { get; set; }

    public int ScanId { get; set; }
    public PostAutoTagScan Scan { get; set; } = null!;

    public AutoTagProvider Provider { get; set; }

    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;
}
