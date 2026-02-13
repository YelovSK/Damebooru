using Bakabooru.Core.Interfaces;
using FFMpegCore;
using Microsoft.Extensions.Logging;

namespace Bakabooru.Processing.Infrastructure;

/// <summary>
/// Unified media processor using FFmpeg for all formats (images, videos, JXL, AVIF, WebP, etc.)
/// </summary>
public class FFmpegProcessor : IImageProcessor
{
    private readonly ILogger<FFmpegProcessor> _logger;
    private static readonly TimeSpan MinVideoCaptureTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxVideoCaptureTime = TimeSpan.FromSeconds(10);

    public FFmpegProcessor(ILogger<FFmpegProcessor> logger)
    {
        _logger = logger;
    }

    public async Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int width, int height, CancellationToken cancellationToken = default)
    {
        try
        {
            var analysis = await FFProbe.AnalyseAsync(sourcePath, null, cancellationToken);
            var hasVideo = analysis.PrimaryVideoStream != null;
            var duration = analysis.Duration;

            if (hasVideo)
            {
                // Video — avoid the first frame (often black/fade-in/logo).
                var captureTime = GetVideoCaptureTime(duration);
                _logger.LogDebug("Taking video snapshot of {Path} at {Time}", sourcePath, captureTime);
                await FFMpegArguments
                    .FromFileInput(sourcePath, true, options => options.Seek(captureTime))
                    .OutputToFile(destinationPath, true, options => options
                        .WithCustomArgument($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease\"")
                        .WithCustomArgument("-frames:v 1")
                    )
                    .ProcessAsynchronously();
            }
            else
            {
                // Image (including JXL, AVIF, WebP) — convert first frame.
                _logger.LogDebug("Converting to thumbnail: {Path}", sourcePath);
                await FFMpegArguments
                    .FromFileInput(sourcePath)
                    .OutputToFile(destinationPath, true, options => options
                        .WithCustomArgument($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease\"")
                        .WithCustomArgument("-frames:v 1")
                    )
                    .ProcessAsynchronously();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnail for {Path}", sourcePath);
            throw;
        }
    }

    private static TimeSpan GetVideoCaptureTime(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return MinVideoCaptureTime;
        }

        // Prefer a frame from ~20% into the video, bounded to avoid seeking too far.
        var preferred = TimeSpan.FromTicks((long)(duration.Ticks * 0.2));
        var clamped = preferred < MinVideoCaptureTime
            ? MinVideoCaptureTime
            : preferred > MaxVideoCaptureTime
                ? MaxVideoCaptureTime
                : preferred;

        // Never seek beyond last frame.
        var safeUpperBound = duration - TimeSpan.FromMilliseconds(50);
        return safeUpperBound > MinVideoCaptureTime && clamped > safeUpperBound
            ? safeUpperBound
            : clamped;
    }

    public async Task<ImageMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var analysis = await FFProbe.AnalyseAsync(filePath, null, cancellationToken);
            return new ImageMetadata
            {
                Width = analysis.PrimaryVideoStream?.Width ?? 0,
                Height = analysis.PrimaryVideoStream?.Height ?? 0,
                Format = analysis.Format.FormatName
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read metadata for {Path}", filePath);
            return new ImageMetadata { Width = 0, Height = 0, Format = "Unknown" };
        }
    }
}
