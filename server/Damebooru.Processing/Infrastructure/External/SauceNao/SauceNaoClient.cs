using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Infrastructure.External.SauceNao;

internal sealed class SauceNaoClient(
    HttpClient httpClient,
    DamebooruConfig config,
    SauceNaoRateCoordinator rateCoordinator,
    ILogger<SauceNaoClient> logger) : ISauceNaoClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly SauceNaoApiConfig _config = config.ExternalApis.SauceNao;
    private readonly SauceNaoRateCoordinator _rateCoordinator = rateCoordinator;
    private readonly ILogger<SauceNaoClient> _logger = logger;

    private static readonly HashSet<string> SupportedUploadContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/bmp",
        "image/webp",
    };

    private static readonly HashSet<string> SupportedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".webp",
    };

    private static readonly ImageUploadPreparationOptions UploadPreparationOptions = new()
    {
        SupportedUploadContentTypes = SupportedUploadContentTypes,
        SupportedUploadExtensions = SupportedUploadExtensions,
    };

    public async Task<SauceNaoSearchResult> SearchAsync(
        Stream fileStream,
        string fileName,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            throw new InvalidOperationException("SauceNAO API key is not configured.");
        }

        await using var uploadStream = await PrepareUploadStreamAsync(fileStream, fileName, contentType, cancellationToken);
        await using var _ = await _rateCoordinator.AcquireAsync(cancellationToken);

        using var formData = new MultipartFormDataContent();
        var fileContent = new StreamContent(uploadStream.Stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(uploadStream.ContentType);
        formData.Add(fileContent, "file", uploadStream.FileName);

        var url = $"/search.php?output_type=2&api_key={Uri.EscapeDataString(_config.ApiKey)}&numres={_config.ResultsCount}&db={_config.Database}&minsim={_config.MinimumSimilarity.ToString(CultureInfo.InvariantCulture)}";
        using var response = await _httpClient.PostAsync(url, formData, cancellationToken);

        var payload = await ReadPayloadAsync(response, cancellationToken);

        if (response.IsSuccessStatusCode && payload.Header.IsSuccess)
        {
            var rateObservation = _rateCoordinator.ObserveSuccess(payload.Header);
            if (rateObservation.RequiresResync)
            {
                _logger.LogWarning(
                    "SauceNAO quota drift detected. ExpectedShortRemaining={ExpectedShortRemaining}, ActualShortRemaining={ActualShortRemaining}, delaying until {BlockedUntilUtc}.",
                    rateObservation.ExpectedShortRemaining,
                    rateObservation.ActualShortRemaining,
                    rateObservation.BlockedUntilUtc);
            }

            return new SauceNaoSearchResult(
                payload.Header.Status,
                payload.Header.Message,
                payload.Header.ResultsRequested,
                payload.Header.ResultsReturned,
                ParseDecimal(payload.Header.ShortLimit),
                payload.Header.ShortRemaining,
                ParseDecimal(payload.Header.LongLimit),
                payload.Header.LongRemaining,
                payload.Results.Select(MapMatch).ToList()
            );
        }

        if (payload.Header.IsShortLimitExceeded)
        {
            var blockedUntilUtc = _rateCoordinator.ObserveShortLimitExceeded();
            _logger.LogWarning(
                "SauceNAO short-term limit exceeded. Blocking further requests until {BlockedUntilUtc}.",
                blockedUntilUtc);
        }
        else
        {
            _rateCoordinator.ObserveFailure();
        }

        LogFailure(response.StatusCode, payload.Header);

        if (!response.IsSuccessStatusCode || !payload.Header.IsSuccess)
        {
            throw new ExternalProviderException(
                provider: AutoTagProvider.SauceNao,
                message: BuildFailureMessage(response.StatusCode, payload.Header),
                rejectsImage: payload.Header.IsNoImageProvided || payload.Header.IsFileTooLarge || payload.Header.IsImageTooSmall,
                retryAfter: payload.Header.IsShortLimitExceeded || payload.Header.IsFailedAttemptsExceeded
                    ? TimeSpan.FromSeconds(30)
                    : null,
                stopCurrentRun: payload.Header.IsDailyLimitExceeded
                    || payload.Header.IsFailedAttemptsExceeded
                    || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        }

        throw new InvalidOperationException("Unreachable SauceNAO response state encountered.");
    }

    private async Task<SauceNaoResponseDto> ReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var payload = JsonSerializer.Deserialize<SauceNaoResponseDto>(content, JsonSerializerOptions.Web);
            if (payload != null)
            {
                return payload;
            }
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Non-JSON 429 body; without a header, assume the short-term rate limit.
            return new SauceNaoResponseDto
            {
                Header = new SauceNaoHeaderDto { Status = -2, Message = content },
            };
        }

        // Usually an HTML error or maintenance page from SauceNAO or its proxy, which passes.
        _rateCoordinator.ObserveFailure();
        var excerpt = content.Length > 300 ? content[..300] + "…" : content;
        throw new ExternalProviderException(
            provider: AutoTagProvider.SauceNao,
            message: $"SauceNAO returned a non-JSON response with HTTP {(int)response.StatusCode}: {excerpt}");
    }

    private void LogFailure(HttpStatusCode statusCode, SauceNaoHeaderDto header)
    {
        _logger.LogWarning(
            "SauceNAO request failed. HttpStatus={HttpStatus}, HeaderStatus={HeaderStatus}, ShortRemaining={ShortRemaining}, LongRemaining={LongRemaining}, Message={Message}",
            (int)statusCode,
            header.Status,
            header.ShortRemaining,
            header.LongRemaining,
            header.Message);
    }

    private static string BuildFailureMessage(HttpStatusCode statusCode, SauceNaoHeaderDto header)
        => $"SauceNAO request failed with HTTP {(int)statusCode}, header status {header.Status}: {header.Message}";

    private static SauceNaoMatch MapMatch(SauceNaoResultDto result)
    {
        var urls = ExternalUrlParser.ParseMany(
            result.Data.Source,
            result.Data.AuthorUrl,
            string.Join(' ', result.Data.ExternalUrls ?? []));

        return new SauceNaoMatch(
            result.Header.IndexId,
            result.Header.IndexName,
            ParseDecimal(result.Header.Similarity) ?? 0m,
            urls,
            result.Data.DanbooruId,
            result.Data.GelbooruId,
            result.Data.PixivId,
            result.Data.PostId);
    }

    private static decimal? ParseDecimal(string? raw)
        => decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static async Task<PreparedUploadStream> PrepareUploadStreamAsync(
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
            throw new ExternalProviderException(
                AutoTagProvider.SauceNao,
                $"SauceNAO upload preparation failed: {ex.Message}",
                rejectsImage: true,
                innerException: ex);
        }
    }
}
