using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;
using Microsoft.Extensions.Logging;
using PhotoSauce.MagicScaler;

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

    // It's 25MB, but using 20MB to be safe.
    private const long SauceNaoMaxUploadBytes = 20L * 1024L * 1024L;
    private static readonly HashSet<string> SupportedUploadContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif",
        "image/bmp",
        "image/webp",
    };

    private static readonly HashSet<string> SupportedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".bmp",
        ".webp",
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

        var payload = await response.Content.ReadFromJsonAsync<SauceNaoResponseDto>(cancellationToken);
        if (payload is null)
        {
            _rateCoordinator.ObserveFailure();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Could not deserialize SauceNAO response: {content}");
        }

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
                isRetryable: IsRetryable(response.StatusCode, payload.Header),
                retryAfter: payload.Header.IsShortLimitExceeded || payload.Header.IsFailedAttemptsExceeded
                    ? TimeSpan.FromSeconds(30)
                    : null,
                stopCurrentRun: payload.Header.IsDailyLimitExceeded || payload.Header.IsFailedAttemptsExceeded || response.StatusCode == HttpStatusCode.TooManyRequests);
        }

        throw new InvalidOperationException("Unreachable SauceNAO response state encountered.");
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
        var resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : fileName;
        var resolvedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var shouldTranscode = RequiresJpegTranscode(resolvedFileName, resolvedContentType)
            || IsFileTooLarge(fileStream);

        if (!shouldTranscode)
        {
            if (fileStream.CanSeek)
            {
                fileStream.Seek(0, SeekOrigin.Begin);
            }

            return new PreparedUploadStream(fileStream, resolvedFileName, resolvedContentType, ownsStream: false);
        }

        var output = new MemoryStream();
        var settings = new ProcessImageSettings();
        settings.TrySetEncoderFormat("image/jpeg");

        if (fileStream.CanSeek)
        {
            fileStream.Seek(0, SeekOrigin.Begin);
        }

        await Task.Run(() => MagicImageProcessor.ProcessImage(fileStream, output, settings), cancellationToken);
        output.Seek(0, SeekOrigin.Begin);

        if (output.Length > SauceNaoMaxUploadBytes)
        {
            await output.DisposeAsync();
            throw new ExternalProviderException(
                AutoTagProvider.SauceNao,
                $"SauceNAO upload remains too large after JPEG conversion ({output.Length} bytes).",
                isRetryable: false);
        }

        return new PreparedUploadStream(output, Path.ChangeExtension(resolvedFileName, ".jpg"), "image/jpeg", ownsStream: true);
    }

    private static bool RequiresJpegTranscode(string fileName, string contentType)
        => !SupportedUploadContentTypes.Contains(contentType)
           && !SupportedUploadExtensions.Contains(Path.GetExtension(fileName));

    private static bool IsFileTooLarge(Stream stream)
        => stream.CanSeek && stream.Length > SauceNaoMaxUploadBytes;

    private static bool IsRetryable(HttpStatusCode statusCode, SauceNaoHeaderDto header)
    {
        if (header.IsNoImageProvided || header.IsFileTooLarge || header.IsImageTooSmall)
        {
            return false;
        }

        return statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private sealed class PreparedUploadStream(Stream stream, string fileName, string contentType, bool ownsStream) : IAsyncDisposable
    {
        private readonly bool _ownsStream = ownsStream;

        public Stream Stream { get; } = stream;
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;

        public ValueTask DisposeAsync() => _ownsStream
            ? Stream.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
