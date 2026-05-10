namespace Damebooru.Core.DTOs;

public sealed class AutoTagDiscoverySettingsDto
{
    public bool SauceNaoEnabled { get; set; }
    public bool IqdbEnabled { get; set; }
    public bool DanbooruEnabled { get; set; }
    public bool GelbooruEnabled { get; set; }
}

public sealed class DuplicateDetectionSettingsDto
{
    public int PerceptualSimilarityThresholdPercent { get; set; }
}
