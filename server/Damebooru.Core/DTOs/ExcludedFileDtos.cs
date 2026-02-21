namespace Damebooru.Core.DTOs;

public record ExcludedFileDto
{
    public int Id { get; init; }
    public int LibraryId { get; init; }
    public string LibraryName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string? ContentHash { get; init; }
    public DateTime ExcludedDate { get; init; }
    public string Reason { get; init; } = string.Empty;
}
