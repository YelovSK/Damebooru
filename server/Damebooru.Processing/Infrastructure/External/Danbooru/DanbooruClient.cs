using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;

namespace Damebooru.Processing.Infrastructure.External.Danbooru;

internal sealed class DanbooruClient : IDanbooruClient
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

    private static IReadOnlyList<ExternalTagData> ParseTags(string? rawTags, TagCategoryKind category)
        => string.IsNullOrWhiteSpace(rawTags)
            ? []
            : rawTags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => new ExternalTagData(tag, category))
                .ToList();

    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
}
