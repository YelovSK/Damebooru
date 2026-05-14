namespace Damebooru.Core.Entities;

public sealed class AiTaggingSettings
{
    public const decimal DefaultSuggestionThreshold = 0.492m;
    public const decimal DefaultApplyThreshold = 0.70m;

    public int Id { get; set; }
    public decimal SuggestionThreshold { get; set; } = DefaultSuggestionThreshold;
    public decimal ApplyThreshold { get; set; } = DefaultApplyThreshold;
}
