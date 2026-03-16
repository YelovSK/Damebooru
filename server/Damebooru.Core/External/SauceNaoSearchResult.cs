namespace Damebooru.Core.External;

public sealed record SauceNaoSearchResult(
    int Status,
    string Message,
    int ResultsRequested,
    int ResultsReturned,
    decimal? ShortLimit,
    decimal? ShortRemaining,
    decimal? LongLimit,
    decimal? LongRemaining,
    IReadOnlyList<SauceNaoMatch> Matches);
