namespace Damebooru.Core.DTOs;

public class AppLogEntryDto
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public string? MessageTemplate { get; set; }
    public string? PropertiesJson { get; set; }
}

public class AppLogListDto
{
    public List<AppLogEntryDto> Items { get; set; } = [];
    public bool HasMore { get; set; }
}
