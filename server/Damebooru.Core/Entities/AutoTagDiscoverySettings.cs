namespace Damebooru.Core.Entities;

public sealed class AutoTagDiscoverySettings
{
    public int Id { get; set; }
    public bool SauceNaoEnabled { get; set; } = true;
    public bool IqdbEnabled { get; set; } = true;
    public bool DanbooruEnabled { get; set; } = true;
    public bool GelbooruEnabled { get; set; } = true;
}
