using Damebooru.Core;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using PhotoSauce.MagicScaler;
using PhotoSauce.NativeCodecs.Libjxl;
using PhotoSauce.NativeCodecs.Libpng;
using PhotoSauce.NativeCodecs.Libwebp;

namespace Damebooru.Processing.Infrastructure;

/// <summary>
/// Media processor using MagicScaler for images and FFmpeg for videos.
/// </summary>
public class MediaProcessor : IMediaFileProcessor
{
    private readonly ILogger<MediaProcessor> _logger;
    private static readonly TimeSpan MinVideoCaptureTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxVideoCaptureTime = TimeSpan.FromSeconds(10);

    public MediaProcessor(ILogger<MediaProcessor> logger)
    {
        _logger = logger;
    }

    public Task GeneratePreviewAsync(string sourcePath, string destinationPath, int maxSize, CancellationToken cancellationToken = default)
        => GenerateImageDerivativeAsync(sourcePath, destinationPath, maxSize, maxSize, CropScaleMode.Max, cancellationToken);

    public Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int size, CancellationToken cancellationToken = default)
        => GenerateImageDerivativeAsync(sourcePath, destinationPath, size, size, CropScaleMode.Crop, cancellationToken);

    private async Task GenerateImageDerivativeAsync(
        string sourcePath,
        string destinationPath,
        int width,
        int height,
        CropScaleMode resizeMode,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Generating derived image for {Path}", sourcePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory) && !Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var extension = Path.GetExtension(sourcePath);
            if (SupportedMedia.IsImage(extension))
            {
                await GenerateImageFileDerivativeAsync(sourcePath, destinationPath, width, height, resizeMode, cancellationToken);
            }
            else
            {
                await GenerateVideoFileDerivativeAsync(sourcePath, destinationPath, width, height, resizeMode, cancellationToken);
            }

            EnsureDerivativeCreated(destinationPath, sourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate derived image for {Path}", sourcePath);
            throw;
        }
    }

    private static async Task GenerateImageFileDerivativeAsync(
        string sourcePath,
        string destinationPath,
        int width,
        int height,
        CropScaleMode resizeMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = new ProcessImageSettings
        {
            Width = width,
            Height = height,
            ResizeMode = resizeMode,
            DecoderOptions = GetSingleFrameDecoderOptions(Path.GetExtension(sourcePath)),
        };

        settings.TrySetEncoderFormat(MediaPaths.GeneratedImageContentType);

        await Task.Run(() => MagicImageProcessor.ProcessImage(sourcePath, destinationPath, settings), cancellationToken);
    }

    private static IDecoderOptions? GetSingleFrameDecoderOptions(string extension)
    {
        var firstFrame = 0..1;
        return extension.ToLowerInvariant() switch
        {
            ".gif" => new GifDecoderOptions(firstFrame, true),
            ".jxl" => new JxlDecoderOptions(firstFrame),
            ".png" => new PngDecoderOptions(firstFrame),
            ".tif" or ".tiff" => new TiffDecoderOptions(firstFrame),
            ".webp" => new WebpDecoderOptions(firstFrame, true, true),
            _ => null,
        };
    }

    private static async Task GenerateVideoFileDerivativeAsync(
        string sourcePath,
        string destinationPath,
        int width,
        int height,
        CropScaleMode resizeMode,
        CancellationToken cancellationToken)
    {
        var analysis = await FFProbe.AnalyseAsync(sourcePath, null, cancellationToken);
        var duration = analysis.Duration;
        var filter = resizeMode == CropScaleMode.Crop
            ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}"
            : $"scale={width}:{height}:force_original_aspect_ratio=decrease";

        await FFMpegArguments
            .FromFileInput(sourcePath, true, options => options.Seek(GetVideoCaptureTime(duration)))
            .OutputToFile(destinationPath, true, options => options
                .WithCustomArgument($"-vf \"{filter}\"")
                .WithCustomArgument("-frames:v 1"))
            .ProcessAsynchronously();
    }

    private static TimeSpan GetVideoCaptureTime(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Never seek at/after EOF.
        var safeUpperBound = duration - TimeSpan.FromMilliseconds(50);
        if (safeUpperBound <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Prefer a frame from ~20% into the video, bounded to avoid seeking too far.
        var preferred = TimeSpan.FromTicks((long)(duration.Ticks * 0.2));
        var clamped = preferred < MinVideoCaptureTime
            ? MinVideoCaptureTime
            : preferred > MaxVideoCaptureTime
                ? MaxVideoCaptureTime
                : preferred;

        return clamped > safeUpperBound ? safeUpperBound : clamped;
    }

    private static void EnsureDerivativeCreated(string destinationPath, string sourcePath)
    {
        if (!File.Exists(destinationPath))
        {
            throw new InvalidOperationException(
                $"Derived image generation completed but did not create output. Source: '{sourcePath}', Destination: '{destinationPath}'.");
        }

        var size = new FileInfo(destinationPath).Length;
        if (size <= 0)
        {
            throw new InvalidOperationException(
                $"Derived image generation created an empty output file. Source: '{sourcePath}', Destination: '{destinationPath}'.");
        }
    }

    public async Task<MediaMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var extension = Path.GetExtension(filePath);
            if (SupportedMedia.IsImage(extension))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = await Task.Run(() => ImageFileInfo.Load(filePath), cancellationToken);
                var hasFrame = info.Frames.Count > 0;
                var frame = hasFrame ? info.Frames[0] : default;

                return new MediaMetadata
                {
                    Width = hasFrame ? frame.Width : 0,
                    Height = hasFrame ? frame.Height : 0,
                    Format = string.IsNullOrWhiteSpace(info.MimeType) ? "Unknown" : info.MimeType,
                };
            }

            var analysis = await FFProbe.AnalyseAsync(filePath, null, cancellationToken);

            return new MediaMetadata
            {
                Width = analysis.PrimaryVideoStream?.Width ?? 0,
                Height = analysis.PrimaryVideoStream?.Height ?? 0,
                Format = analysis.Format.FormatName,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read metadata for {Path}", filePath);
            return new MediaMetadata
            {
                Width = 0,
                Height = 0,
                Format = "Unknown",
            };
        }
    }
}
