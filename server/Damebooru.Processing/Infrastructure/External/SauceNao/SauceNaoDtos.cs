using System.Text.Json.Serialization;

namespace Damebooru.Processing.Infrastructure.External.SauceNao;

internal sealed class SauceNaoResponseDto
{
    [JsonPropertyName("header")]
    public SauceNaoHeaderDto Header { get; set; } = new();

    [JsonPropertyName("results")]
    public List<SauceNaoResultDto> Results { get; set; } = [];
}

internal sealed class SauceNaoHeaderDto
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("results_requested")]
    public int ResultsRequested { get; set; }

    [JsonPropertyName("results_returned")]
    public int ResultsReturned { get; set; }

    [JsonPropertyName("short_limit")]
    public string? ShortLimit { get; set; }

    [JsonPropertyName("long_limit")]
    public string? LongLimit { get; set; }

    [JsonPropertyName("short_remaining")]
    public int? ShortRemaining { get; set; }

    [JsonPropertyName("long_remaining")]
    public int? LongRemaining { get; set; }

    public bool IsSuccess => Status >= 0;

    // Maybe there's a better way, but based on the response I don't see it.
    public bool IsShortLimitExceeded => !IsSuccess && Message?.Contains("Search Rate Too High", StringComparison.InvariantCultureIgnoreCase) == true;
    public bool IsDailyLimitExceeded => !IsSuccess && Message?.Contains("Daily Search Limit Exceeded", StringComparison.InvariantCultureIgnoreCase) == true;
    public bool IsFailedAttemptsExceeded => !IsSuccess && Message?.Contains("Too many failed search attempts", StringComparison.InvariantCultureIgnoreCase) == true;
}

internal sealed class SauceNaoResultDto
{
    [JsonPropertyName("header")]
    public SauceNaoResultHeaderDto Header { get; set; } = new();

    [JsonPropertyName("data")]
    public SauceNaoResultDataDto Data { get; set; } = new();
}

internal sealed class SauceNaoResultHeaderDto
{
    [JsonPropertyName("similarity")]
    public string Similarity { get; set; } = string.Empty;

    [JsonPropertyName("index_id")]
    public int IndexId { get; set; }

    [JsonPropertyName("index_name")]
    public string IndexName { get; set; } = string.Empty;
}

internal sealed class SauceNaoResultDataDto
{
    [JsonPropertyName("ext_urls")]
    public List<string>? ExternalUrls { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("author_name")]
    public string? AuthorName { get; set; }

    [JsonPropertyName("author_url")]
    public string? AuthorUrl { get; set; }

    [JsonPropertyName("danbooru_id")]
    public long? DanbooruId { get; set; }

    [JsonPropertyName("gelbooru_id")]
    public long? GelbooruId { get; set; }

    [JsonPropertyName("pixiv_id")]
    public long? PixivId { get; set; }

    [JsonPropertyName("post_id")]
    public long? PostId { get; set; }
}
