using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure.External.Shared;
using PhotoSauce.MagicScaler;

namespace Damebooru.Processing.Infrastructure.External.SauceNao;

internal sealed class SauceNaoClient(HttpClient httpClient, DamebooruConfig config) : ISauceNaoClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly SauceNaoApiConfig _config = config.ExternalApis.SauceNao;

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

        using var formData = new MultipartFormDataContent();
        var fileContent = new StreamContent(uploadStream.Stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(uploadStream.ContentType);
        formData.Add(fileContent, "file", uploadStream.FileName);

        var url = $"/search.php?output_type=2&api_key={Uri.EscapeDataString(_config.ApiKey)}&numres={_config.ResultsCount}&db={_config.Database}&minsim={_config.MinimumSimilarity.ToString(CultureInfo.InvariantCulture)}";
        using var response = await _httpClient.PostAsync(url, formData, cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<SauceNaoResponseDto>(cancellationToken);
        if (payload is null)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Could not deserialize SauceNAO response: {content}");
        }

        if (!response.IsSuccessStatusCode || !payload.Header.IsSuccess)
        {
            throw new ExternalProviderException(
                provider: AutoTagProvider.SauceNao,
                message: $"SauceNAO request failed with status code {(int)response.StatusCode}.",
                isRetryable: response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500,
                retryAfter: payload.Header.IsShortLimitExceeded || payload.Header.IsFailedAttemptsExceeded
                    ? TimeSpan.FromSeconds(30)
                    : null,
                stopCurrentRun: payload.Header.IsDailyLimitExceeded);
        }

        return new SauceNaoSearchResult(
            payload.Header.Status,
            payload.Header.Message,
            payload.Header.ResultsRequested,
            payload.Header.ResultsReturned,
            ParseDecimal(payload.Header.ShortLimit),
            ParseDecimal(payload.Header.LongLimit),
            payload.Results.Select(MapMatch).ToList()
        );
    }

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

        if (!RequiresJpegTranscode(resolvedFileName, resolvedContentType))
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

        return new PreparedUploadStream(output, Path.ChangeExtension(resolvedFileName, ".jpg"), "image/jpeg", ownsStream: true);
    }

    private static bool RequiresJpegTranscode(string fileName, string contentType)
        => string.Equals(contentType, "image/jxl", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetExtension(fileName), ".jxl", StringComparison.OrdinalIgnoreCase);

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
