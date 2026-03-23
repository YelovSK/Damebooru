using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;

namespace Damebooru.Processing.Infrastructure.External.SauceNao;

internal sealed class SauceNaoDiscoveryClient(ISauceNaoClient sauceNaoClient, DamebooruConfig config) : IExternalPostDiscoveryClient
{
    private readonly ISauceNaoClient _sauceNaoClient = sauceNaoClient;
    private readonly decimal _minimumSimilarity = config.ExternalApis.SauceNao.MinimumSimilarity;

    public AutoTagProvider Provider => AutoTagProvider.SauceNao;

    public async Task<ExternalDiscoveryResult> DiscoverAsync(PostDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(context.FilePath);
        var result = await _sauceNaoClient.SearchAsync(stream, Path.GetFileName(context.FilePath), context.ContentType, cancellationToken);
        var acceptedMatches = result.Matches
            .Where(match => match.Similarity >= _minimumSimilarity)
            .ToList();

        var matches = acceptedMatches
            .SelectMany(match => EnumerateDiscoveredUrls(match).Select(url => new DiscoveredUrlMatch(url, match.Similarity)))
            .GroupBy(match => match.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.Score).First())
            .ToList();

        return new ExternalDiscoveryResult(Provider, matches);
    }

    private static IEnumerable<string> EnumerateDiscoveredUrls(SauceNaoMatch match)
    {
        foreach (var url in match.ExternalUrls)
        {
            yield return url;
        }

        if (match.DanbooruPostId.HasValue)
        {
            yield return $"https://danbooru.donmai.us/posts/{match.DanbooruPostId.Value}";
        }

        if (match.GelbooruPostId.HasValue)
        {
            yield return $"https://gelbooru.com/index.php?page=post&s=view&id={match.GelbooruPostId.Value}";
        }
    }
}
