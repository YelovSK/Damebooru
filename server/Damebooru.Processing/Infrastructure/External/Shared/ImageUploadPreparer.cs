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
            || IsFileTooLarge(fileStream, options.MaxUploadBytes)
            || ExceedsMaxDimension(fileStream, options.MaxDimension);

        if (!shouldTranscode)
        {
            if (fileStream.CanSeek)
            {
                fileStream.Seek(0, SeekOrigin.Begin);
            }

            return new PreparedUploadStream(fileStream, resolvedFileName, resolvedContentType, ownsStream: false);
        }

        var output = new MemoryStream();
        var settings = new ProcessImageSettings
        {
            Width = options.MaxDimension,
            Height = options.MaxDimension,
            ResizeMode = CropScaleMode.Max,
        };
        settings.TrySetEncoderFormat("image/jpeg");

        if (fileStream.CanSeek)
        {
            fileStream.Seek(0, SeekOrigin.Begin);
        }

        try
        {
            await Task.Run(() => MagicImageProcessor.ProcessImage(fileStream, output, settings), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.DisposeAsync();
            throw new ImageUploadPreparationException("Image upload could not be converted to a supported format.", ex);
        }

        output.Seek(0, SeekOrigin.Begin);

        if (output.Length > options.MaxUploadBytes)
        {
            await output.DisposeAsync();
            throw new ImageUploadPreparationException($"Image upload remains too large after conversion ({output.Length} bytes).");
        }

        return new PreparedUploadStream(output, Path.ChangeExtension(resolvedFileName, ".jpg"), "image/jpeg", ownsStream: true);
    }

    private static bool IsSupportedUploadFormat(string fileName, string contentType, ImageUploadPreparationOptions options)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension)
            ? options.SupportedUploadContentTypes.Contains(contentType)
            : options.SupportedUploadExtensions.Contains(extension);
    }

    private static bool IsFileTooLarge(Stream stream, long maxUploadBytes)
        => stream.CanSeek && stream.Length > maxUploadBytes;

    private static bool ExceedsMaxDimension(Stream stream, int maxDimension)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            var frame = ImageFileInfo.Load(stream).Frames[0];
            return Math.Max(frame.Width, frame.Height) > maxDimension;
        }
        catch (Exception)
        {
            // Unreadable headers: upload as-is and let the provider decide.
            return false;
        }
        finally
        {
            stream.Seek(0, SeekOrigin.Begin);
        }
    }
}
