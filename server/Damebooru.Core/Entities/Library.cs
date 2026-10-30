using System;

namespace Damebooru.Core.Entities;

public class Library
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public LibraryKind Kind { get; set; } = LibraryKind.Library;
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromHours(1);
}
