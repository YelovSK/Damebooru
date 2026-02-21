namespace Damebooru.Core.Interfaces;

public class JobState
{
    public string Phase { get; set; } = string.Empty;
    public int? Processed { get; set; }
    public int? Total { get; set; }
    public int? Succeeded { get; set; }
    public int? Failed { get; set; }
    public int? Skipped { get; set; }
    public string? Summary { get; set; }
}

