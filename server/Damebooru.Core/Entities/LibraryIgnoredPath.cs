namespace Damebooru.Core.Entities;

/// <summary>
/// A library-relative directory prefix that should be excluded from scanning.
/// </summary>
public class LibraryIgnoredPath
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public Library Library { get; set; } = null!;
    public string RelativePathPrefix { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}
