using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Server.DTOs;

public class CreateLibraryDto
{
    [Required]
    public string Path { get; set; } = string.Empty;
}

public class LibraryDto
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public double ScanIntervalHours { get; set; }
}
