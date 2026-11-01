namespace Damebooru.Core.Interfaces;

public sealed record JobState
{
    public string? ActivityText { get; init; }
    public string? FinalText { get; init; }
    public int? ProgressCurrent { get; init; }
    public int? ProgressTotal { get; init; }
}
