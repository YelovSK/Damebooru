using Damebooru.Core.External;
using PhotoSauce.MagicScaler;

namespace Damebooru.Processing.Infrastructure.External.Shared;

internal static class ImageUploadPreparer
{
    public static async Task<PreparedUploadStream> PrepareAsync(
        Stream fileStream,
        string fileName,
        string? contentType,
        ImageUploadPreparationOptions options,
        CancellationToken cancellationToken)
    {
        var resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : fileName;
        var resolvedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        var shouldTranscode = !IsSupportedUploadFormat(resolvedFileName, resolvedContentType, options)
            || IsFileTooLarge(fileStream, options.MaxUploadBytes);

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
        if (options.MaxDimension.HasValue)
        {
            settings.Width = options.MaxDimension.Value;
            settings.Height = options.MaxDimension.Value;
            settings.ResizeMode = CropScaleMode.Max;
        }
        settings.TrySetEncoderFormat("image/jpeg");

        if (fileStream.CanSeek)
        {
            fileStream.Seek(0, SeekOrigin.Begin);
        }

        await Task.Run(() => MagicImageProcessor.ProcessImage(fileStream, output, settings), cancellationToken);
        output.Seek(0, SeekOrigin.Begin);

        if (output.Length > options.MaxUploadBytes)
        {
            await output.DisposeAsync();
            throw new ExternalProviderException(
                options.Provider,
                $"{options.ProviderName} upload remains too large after conversion ({output.Length} bytes).",
                isRetryable: false);
        }

        return new PreparedUploadStream(output, Path.ChangeExtension(resolvedFileName, ".jpg"), "image/jpeg", ownsStream: true);
    }

    private static bool IsSupportedUploadFormat(string fileName, string contentType, ImageUploadPreparationOptions options)
        => options.SupportedUploadContentTypes.Contains(contentType)
           || options.SupportedUploadExtensions.Contains(Path.GetExtension(fileName));

    private static bool IsFileTooLarge(Stream stream, long maxUploadBytes)
        => stream.CanSeek && stream.Length > maxUploadBytes;
}
