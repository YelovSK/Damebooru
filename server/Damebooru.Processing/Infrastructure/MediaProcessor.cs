using Damebooru.Core;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using PhotoSauce.MagicScaler;

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

    public async Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int maxSize, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Generating thumbnail for {Path}", sourcePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory) && !Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var extension = Path.GetExtension(sourcePath);
            if (SupportedMedia.IsImage(extension))
            {
                await GenerateImageThumbnailAsync(sourcePath, destinationPath, maxSize, cancellationToken);
            }
            else
            {
                await GenerateVideoThumbnailAsync(sourcePath, destinationPath, maxSize, cancellationToken);
            }

            EnsureThumbnailCreated(destinationPath, sourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnail for {Path}", sourcePath);
            throw;
        }
    }

    private static async Task GenerateImageThumbnailAsync(string sourcePath, string destinationPath, int maxSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = new ProcessImageSettings
        {
            Width = maxSize,
            Height = maxSize,
            ResizeMode = CropScaleMode.Max,
        };

        settings.TrySetEncoderFormat(MediaPaths.ThumbnailContentType);

        await Task.Run(() => MagicImageProcessor.ProcessImage(sourcePath, destinationPath, settings), cancellationToken);
    }

    private static async Task GenerateVideoThumbnailAsync(string sourcePath, string destinationPath, int maxSize, CancellationToken cancellationToken)
    {
        var analysis = await FFProbe.AnalyseAsync(sourcePath, null, cancellationToken);
        var duration = analysis.Duration;

        await FFMpegArguments
            .FromFileInput(sourcePath, true, options => options.Seek(GetVideoCaptureTime(duration)))
            .OutputToFile(destinationPath, true, options => options
                .WithCustomArgument($"-vf \"scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease\"")
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

    private static void EnsureThumbnailCreated(string destinationPath, string sourcePath)
    {
        if (!File.Exists(destinationPath))
        {
            throw new InvalidOperationException(
                $"Thumbnail generation completed but did not create output. Source: '{sourcePath}', Destination: '{destinationPath}'.");
        }

        var size = new FileInfo(destinationPath).Length;
        if (size <= 0)
        {
            throw new InvalidOperationException(
                $"Thumbnail generation created an empty output file. Source: '{sourcePath}', Destination: '{destinationPath}'.");
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
