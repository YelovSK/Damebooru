using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

public class PostAutoTagScanTag
{
    public int Id { get; set; }

    public int ScanId { get; set; }
    public PostAutoTagScan Scan { get; set; } = null!;

    public AutoTagProvider Provider { get; set; }
    public long? ExternalPostId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public TagCategoryKind Category { get; set; } = TagCategoryKind.General;
}
