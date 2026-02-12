using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Server.DTOs;

public class CreateLibraryDto
{
    public string? Name { get; set; }

    [Required]
    public string Path { get; set; } = string.Empty;
}

public class RenameLibraryDto
{
    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;
}

public class LibraryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public double ScanIntervalHours { get; set; }
    public int PostCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastImportDate { get; set; }
}
