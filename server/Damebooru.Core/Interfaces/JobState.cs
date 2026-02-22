namespace Damebooru.Core.Interfaces;

public class JobState
{
    public string? ActivityText { get; set; }
    public string? FinalText { get; set; }
    public int? ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }
    public int? ResultSchemaVersion { get; set; }
    public string? ResultJson { get; set; }
}
