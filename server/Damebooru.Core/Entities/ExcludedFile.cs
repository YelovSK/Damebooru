namespace Damebooru.Core.Entities;

/// <summary>
/// Tracks files excluded from scanning (e.g. duplicate resolved by user).
/// The file stays on disk but won't be re-imported.
/// </summary>
public class ExcludedFile
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public Library Library { get; set; } = null!;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime ExcludedDate { get; set; }
    public string Reason { get; set; } = string.Empty;
}
