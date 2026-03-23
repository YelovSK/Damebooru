using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;

namespace Damebooru.Processing.Infrastructure.External.Danbooru;

internal sealed partial class DanbooruClient : IDanbooruClient
{
    private readonly HttpClient _httpClient;

    public AutoTagProvider Provider => AutoTagProvider.Danbooru;

    public DanbooruClient(HttpClient httpClient, DamebooruConfig config)
    {
        _httpClient = httpClient;

        var apiConfig = config.ExternalApis.Danbooru;
        if (!string.IsNullOrWhiteSpace(apiConfig.Username) && !string.IsNullOrWhiteSpace(apiConfig.ApiKey))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiConfig.Username}:{apiConfig.ApiKey}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public async Task<ExternalPostDetails?> GetPostDetailsAsync(long postId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"/posts/{postId}.json", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalProviderException(
                Provider,
                $"Danbooru request failed with status code {(int)response.StatusCode}.",
                IsRetryable(response.StatusCode));
        }

        var payload = await response.Content.ReadFromJsonAsync<DanbooruPostDto>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Danbooru returned an empty response.");

        var sourceUrls = ExternalUrlParser.ParseMany(payload.Source);
        var tags = new List<ExternalTagData>();
        tags.AddRange(ParseTags(payload.TagStringGeneral, TagCategoryKind.General));
        tags.AddRange(ParseTags(payload.TagStringArtist, TagCategoryKind.Artist));
        tags.AddRange(ParseTags(payload.TagStringCharacter, TagCategoryKind.Character));
        tags.AddRange(ParseTags(payload.TagStringCopyright, TagCategoryKind.Copyright));
        tags.AddRange(ParseTags(payload.TagStringMeta, TagCategoryKind.Meta));

        return new ExternalPostDetails(
            Provider,
            payload.Id,
            $"https://danbooru.donmai.us/posts/{payload.Id}",
            sourceUrls,
            tags);
    }

    public ExternalPostReference? TryParseReference(string url, decimal score)
    {
        var match = DanbooruUrlRegex().Match(url);
        return match.Success && long.TryParse(match.Groups[1].Value, out var postId)
            ? new ExternalPostReference(Provider, postId, $"https://danbooru.donmai.us/posts/{postId}", score)
            : null;
    }

    public async Task<ExternalDiscoveryResult> DiscoverAsync(PostDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Md5Hash))
        {
            return new ExternalDiscoveryResult(Provider, []);
        }

        using var response = await _httpClient.GetAsync($"/posts.json?tags=md5:{Uri.EscapeDataString(context.Md5Hash)}&limit=1", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalProviderException(
                Provider,
                $"Danbooru md5 discovery failed with status code {(int)response.StatusCode}.",
                IsRetryable(response.StatusCode));
        }

        var payload = await response.Content.ReadFromJsonAsync<List<DanbooruPostDto>>(cancellationToken: cancellationToken) ?? [];
        var post = payload.FirstOrDefault();
        if (post == null)
        {
            return new ExternalDiscoveryResult(Provider, []);
        }

        var canonicalUrl = $"https://danbooru.donmai.us/posts/{post.Id}";
        return new ExternalDiscoveryResult(Provider, [new DiscoveredUrlMatch(canonicalUrl, 1m)]);
    }

    private static IReadOnlyList<ExternalTagData> ParseTags(string? rawTags, TagCategoryKind category)
        => string.IsNullOrWhiteSpace(rawTags)
            ? []
            : rawTags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => new ExternalTagData(tag, category))
                .ToList();

    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    [GeneratedRegex(@"https?://danbooru\.donmai\.us/posts/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DanbooruUrlRegex();
}
