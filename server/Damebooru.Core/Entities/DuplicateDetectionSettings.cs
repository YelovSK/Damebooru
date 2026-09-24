namespace Damebooru.Core.Entities;

public sealed class DuplicateDetectionSettings
{
    public const int DefaultPerceptualSimilarityThresholdPercent = 36;

    public int Id { get; set; }
    public int PerceptualSimilarityThresholdPercent { get; set; } = DefaultPerceptualSimilarityThresholdPercent;
}
