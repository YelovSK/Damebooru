using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;

namespace Damebooru.Processing.Infrastructure.External.AiTagging;

internal sealed class AiTaggingClient(HttpClient httpClient, DamebooruConfig config) : IAiTaggingClient
{
    private static readonly ISet<string> SupportedUploadContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/bmp",
        "image/gif",
    };

    private static readonly ISet<string> SupportedUploadExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".gif",
    };

    private static readonly ImageUploadPreparationOptions UploadPreparationOptions = new()
    {
        SupportedUploadContentTypes = SupportedUploadContentTypes,
        SupportedUploadExtensions = SupportedUploadExtensions,
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly AiTaggingConfig _config = config.AiTagging;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("/ready", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<AiTaggingResult> TagAsync(
        Stream fileStream,
        string fileName,
        string? contentType = null,
        decimal? threshold = null,
        decimal? minConfidence = null,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        if (!_config.Enabled)
        {
            throw new InvalidOperationException("AI tagging is not enabled.");
        }

        await using var preparedUpload = await PrepareUploadAsync(fileStream, fileName, contentType, cancellationToken);
        using var formData = new MultipartFormDataContent();
        var fileContent = new StreamContent(preparedUpload.Stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(preparedUpload.ContentType);
        formData.Add(fileContent, "file", preparedUpload.FileName);
        if (threshold == null)
        {
            throw new InvalidOperationException("AI tagging threshold is required.");
        }

        formData.Add(new StringContent(ToInvariantString(threshold.Value)), "threshold");
        formData.Add(new StringContent(ToInvariantString(minConfidence ?? _config.MinConfidence)), "min_confidence");
        formData.Add(new StringContent((topK ?? _config.TopK).ToString(CultureInfo.InvariantCulture)), "top_k");

        using var response = await _httpClient.PostAsync("/tag", formData, cancellationToken);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new InvalidOperationException("AI tagging service is not ready.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"AI tagging request failed with HTTP {(int)response.StatusCode}: {content}");
        }

        var payload = await response.Content.ReadFromJsonAsync<AiTaggingResponseDto>(cancellationToken);
        if (payload is null)
        {
            throw new InvalidOperationException("Could not deserialize AI tagging response.");
        }

        return new AiTaggingResult(
            payload.Model,
            payload.Provider,
            payload.Threshold,
            payload.MinConfidence,
            payload.ElapsedMilliseconds,
            payload.Tags
                .Select(tag => new AiTagData(
                    tag.Name,
                    tag.Score,
                    MapCategory(tag.Category),
                    tag.Category))
                .ToList());
    }

    private static string ToInvariantString(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static TagCategoryKind MapCategory(string category)
        => category.ToLowerInvariant() switch
        {
            "artist" => TagCategoryKind.Artist,
            "copyright" => TagCategoryKind.Copyright,
            "character" => TagCategoryKind.Character,
            "meta" => TagCategoryKind.Meta,
            _ => TagCategoryKind.General,
        };

    private sealed class AiTaggingResponseDto
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("threshold")]
        public decimal Threshold { get; set; }

        [JsonPropertyName("min_confidence")]
        public decimal MinConfidence { get; set; }

        [JsonPropertyName("elapsed_ms")]
        public decimal ElapsedMilliseconds { get; set; }

        [JsonPropertyName("tags")]
        public List<AiTagDto> Tags { get; set; } = [];
    }

    private sealed class AiTagDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public decimal Score { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "general";
    }

    private static async Task<PreparedUploadStream> PrepareUploadAsync(
        Stream fileStream,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ImageUploadPreparer.PrepareAsync(fileStream, fileName, contentType, UploadPreparationOptions, cancellationToken);
        }
        catch (ImageUploadPreparationException ex)
        {
            throw new InvalidOperationException($"AI tagging upload preparation failed: {ex.Message}", ex);
        }
    }
}
