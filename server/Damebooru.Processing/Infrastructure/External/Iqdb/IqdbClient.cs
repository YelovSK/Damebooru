using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;

namespace Damebooru.Processing.Infrastructure.External.Iqdb;

internal sealed partial class IqdbClient(HttpClient httpClient, DamebooruConfig config) : IExternalPostDiscoveryClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly decimal _minimumSimilarity = config.ExternalApis.Iqdb.MinimumSimilarity;

    private const long IqdbMaxUploadBytes = 8L * 1024L * 1024L;
    private const int IqdbMaxDimension = 7500;
    private static readonly HashSet<string> SupportedUploadContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif",
    };

    private static readonly HashSet<string> SupportedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
    };

    private static readonly ImageUploadPreparationOptions UploadPreparationOptions = new()
    {
        ProviderName = "IQDB",
        Provider = AutoTagProvider.Iqdb,
        MaxUploadBytes = IqdbMaxUploadBytes,
        MaxDimension = IqdbMaxDimension,
        SupportedUploadContentTypes = SupportedUploadContentTypes,
        SupportedUploadExtensions = SupportedUploadExtensions,
    };

    public AutoTagProvider Provider => AutoTagProvider.Iqdb;

    public async Task<ExternalDiscoveryResult> DiscoverAsync(PostDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        await using var uploadStream = await PrepareUploadStreamAsync(context, cancellationToken);
        using var formData = new MultipartFormDataContent();
        var fileContent = new StreamContent(uploadStream.Stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(uploadStream.ContentType);
        formData.Add(fileContent, "file", uploadStream.FileName);

        using var response = await _httpClient.PostAsync("/", formData, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ExternalDiscoveryResult(Provider, []);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalProviderException(
                Provider,
                $"IQDB discovery failed with status code {(int)response.StatusCode}.",
                response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var matches = ParseMatches(html)
            .Where(match => match.Similarity >= _minimumSimilarity)
            .ToList();

        var urls = matches
            .Select(match => match.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ExternalDiscoveryResult(Provider, matches.Select(match => new DiscoveredUrlMatch(match.Url, match.Similarity)).ToList());
    }

    private static IReadOnlyList<IqdbMatchInfo> ParseMatches(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);

        ThrowIfIqdbError(document.DocumentElement);

        var matches = new List<IqdbMatchInfo>();
        foreach (var matchNode in EnumerateMatchNodes(document.DocumentElement))
        {
            var href = matchNode.QuerySelector("tr:nth-child(2) > td > a")?.GetAttribute("href")
                ?? matchNode.QuerySelector("tr:nth-child(1) > td > a")?.GetAttribute("href");

            var normalizedUrl = EnsureScheme(WebUtility.HtmlDecode(href));
            if (normalizedUrl == null)
            {
                continue;
            }

            var similarityText = matchNode.QuerySelector("tr:nth-child(5) > td")?.TextContent
                ?? matchNode.QuerySelector("tr:nth-child(4) > td")?.TextContent;

            if (!TryParseSimilarity(similarityText, out var similarity))
            {
                continue;
            }

            matches.Add(new IqdbMatchInfo(normalizedUrl, similarity));
        }

        return matches;
    }

    private static IEnumerable<IElement> EnumerateMatchNodes(IElement root)
    {
        var mainResults = root.QuerySelectorAll("#pages > div").ToList();
        if (mainResults.Count > 0)
        {
            mainResults.RemoveAt(0); // your image block
        }

        foreach (var node in mainResults)
        {
            var matchTypeText = node.QuerySelector("tr:nth-child(1) > th")?.TextContent?.Trim();
            if (matchTypeText is "Best match" or "Additional match" or "Possible match")
            {
                yield return node;
            }
        }

        foreach (var node in root.QuerySelectorAll("#more1 > div.pages > div"))
        {
            yield return node;
        }
    }

    private static void ThrowIfIqdbError(IElement root)
    {
        var errorText = root.QuerySelector(".err")?.TextContent;
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return;
        }

        throw new ExternalProviderException(
            AutoTagProvider.Iqdb,
            $"IQDB discovery failed: {errorText.Trim()}",
            isRetryable: errorText.Contains("HTTP request failed", StringComparison.OrdinalIgnoreCase));
    }

    private static string? EnsureScheme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.ToString() : null;
    }

    private static bool TryParseSimilarity(string? value, out decimal similarity)
    {
        similarity = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var percentText = value.Split('%', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return percentText != null
            && decimal.TryParse(percentText, NumberStyles.Any, CultureInfo.InvariantCulture, out similarity);
    }

    private static async Task<PreparedUploadStream> PrepareUploadStreamAsync(PostDiscoveryContext context, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(context.FilePath);
        return await ImageUploadPreparer.PrepareAsync(
            stream,
            Path.GetFileName(context.FilePath),
            context.ContentType,
            UploadPreparationOptions,
            cancellationToken);
    }

    private sealed record IqdbMatchInfo(string Url, decimal Similarity);
}
