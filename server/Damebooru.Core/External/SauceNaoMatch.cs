namespace Damebooru.Core.External;

public sealed record SauceNaoMatch(
    int IndexId,
    string IndexName,
    decimal Similarity,
    IReadOnlyList<string> ExternalUrls,
    long? DanbooruPostId,
    long? GelbooruPostId,
    long? PixivPostId,
    long? FallbackPostId);
