namespace Damebooru.Core.Entities;

/// <summary>
/// A library-relative directory prefix that should be skipped by the auto-tagging job.
/// </summary>
public class LibraryAutoTagExcludedPath
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public Library Library { get; set; } = null!;
    public string RelativePathPrefix { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}
