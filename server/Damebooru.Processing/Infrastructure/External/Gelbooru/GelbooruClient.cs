using System.Net;
using System.Text.Json;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;

namespace Damebooru.Processing.Infrastructure.External.Gelbooru;

internal sealed class GelbooruClient : IGelbooruClient
{
    private readonly HttpClient _httpClient;
    private readonly GelbooruApiConfig _config;

    public AutoTagProvider Provider => AutoTagProvider.Gelbooru;

    public GelbooruClient(HttpClient httpClient, DamebooruConfig config)
    {
        _httpClient = httpClient;
        _config = config.ExternalApis.Gelbooru;
    }

    public async Task<ExternalPostDetails?> GetPostDetailsAsync(long postId, CancellationToken cancellationToken = default)
    {
        var postResponse = await GetJsonAsync(BuildQuery(new Dictionary<string, string>
        {
            ["page"] = "dapi",
            ["s"] = "post",
            ["q"] = "index",
            ["id"] = postId.ToString(),
            ["json"] = "1"
        }), cancellationToken);

        if (postResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!postResponse.IsSuccessStatusCode)
        {
            throw new ExternalProviderException(
                Provider,
                $"Gelbooru request failed with status code {(int)postResponse.StatusCode}.",
                IsRetryable(postResponse.StatusCode));
        }

        using var postDocument = await JsonDocument.ParseAsync(await postResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!TryGetFirstPost(postDocument.RootElement, out var postElement))
        {
            return null;
        }

        var id = GetInt64(postElement, "id") ?? postId;
        var sourceUrls = ExternalUrlParser.ParseMany(GetString(postElement, "source"));
        var tagNames = (GetString(postElement, "tags") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tags = await GetTagDetailsAsync(tagNames, cancellationToken);

        return new ExternalPostDetails(
            Provider,
            id,
            $"https://gelbooru.com/index.php?page=post&s=view&id={id}",
            sourceUrls,
            tags);
    }

    private async Task<IReadOnlyList<ExternalTagData>> GetTagDetailsAsync(string[] tagNames, CancellationToken cancellationToken)
    {
        if (tagNames.Length == 0)
        {
            return [];
        }

        var response = await GetJsonAsync(BuildQuery(new Dictionary<string, string>
        {
            ["page"] = "dapi",
            ["s"] = "tag",
            ["q"] = "index",
            ["names"] = string.Join(' ', tagNames),
            ["json"] = "1"
        }), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalProviderException(
                Provider,
                $"Gelbooru tag request failed with status code {(int)response.StatusCode}.",
                IsRetryable(response.StatusCode));
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var map = new Dictionary<string, TagCategoryKind>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("tag", out var tagsElement))
        {
            foreach (var tagElement in EnumeratePossiblyWrappedArray(tagsElement))
            {
                var name = GetString(tagElement, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                map[name] = MapCategory(GetInt32(tagElement, "type") ?? 0);
            }
        }

        return tagNames
            .Select(name => new ExternalTagData(name, map.GetValueOrDefault(name, TagCategoryKind.General)))
            .ToList();
    }

    private async Task<HttpResponseMessage> GetJsonAsync(string queryString, CancellationToken cancellationToken)
        => await _httpClient.GetAsync($"/index.php?{queryString}", cancellationToken);

    private string BuildQuery(Dictionary<string, string> parameters)
    {
        if (!string.IsNullOrWhiteSpace(_config.UserId) && !string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            parameters["user_id"] = _config.UserId;
            parameters["api_key"] = _config.ApiKey;
        }

        return string.Join("&", parameters.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private static bool TryGetFirstPost(JsonElement root, out JsonElement post)
    {
        if (root.TryGetProperty("post", out var postElement))
        {
            foreach (var candidate in EnumeratePossiblyWrappedArray(postElement))
            {
                post = candidate;
                return true;
            }
        }

        post = default;
        return false;
    }

    private static IEnumerable<JsonElement> EnumeratePossiblyWrappedArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                yield return child;
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numberValue))
        {
            return numberValue;
        }

        return int.TryParse(property.GetString(), out var stringValue) ? stringValue : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numberValue))
        {
            return numberValue;
        }

        return long.TryParse(property.GetString(), out var stringValue) ? stringValue : null;
    }

    private static TagCategoryKind MapCategory(int type)
        => type switch
        {
            1 => TagCategoryKind.Artist,
            3 => TagCategoryKind.Copyright,
            4 => TagCategoryKind.Character,
            5 => TagCategoryKind.Meta,
            _ => TagCategoryKind.General,
        };

    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
}
