using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.DTOs;

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

public class AddLibraryIgnoredPathDto
{
    [Required]
    [MinLength(1)]
    public string Path { get; set; } = string.Empty;
}

public class LibraryIgnoredPathDto
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}

public class AddLibraryIgnoredPathResultDto
{
    public LibraryIgnoredPathDto IgnoredPath { get; set; } = new();
    public int RemovedPostCount { get; set; }
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
    public List<LibraryIgnoredPathDto> IgnoredPaths { get; set; } = [];
}
