using System;

namespace Bakabooru.Core.Entities;

public class Library
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromHours(1);
}
