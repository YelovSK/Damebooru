using Bakabooru.Core;
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
            var detectedContentType = DetectContentType(
                analysis.Format.FormatName,
                analysis.PrimaryVideoStream?.CodecName);

            return new ImageMetadata
            {
                Width = analysis.PrimaryVideoStream?.Width ?? 0,
                Height = analysis.PrimaryVideoStream?.Height ?? 0,
                Format = analysis.Format.FormatName,
                ContentType = detectedContentType
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read metadata for {Path}", filePath);
            return new ImageMetadata
            {
                Width = 0,
                Height = 0,
                Format = "Unknown",
                ContentType = SupportedMedia.GetMimeType(Path.GetExtension(filePath))
            };
        }
    }

    private static string? DetectContentType(string? formatName, string? codecName)
    {
        var format = (formatName ?? string.Empty).ToLowerInvariant();
        var codec = (codecName ?? string.Empty).ToLowerInvariant();

        // FFprobe format names can be comma-separated aliases (e.g. "mov,mp4,m4a,3gp,3g2,mj2").
        if (format.Contains("png") || codec == "png") return "image/png";
        if (format.Contains("jpeg") || format.Contains("mjpeg") || codec == "mjpeg") return "image/jpeg";
        if (format.Contains("gif") || codec == "gif") return "image/gif";
        if (format.Contains("webp") || codec == "webp") return "image/webp";
        if (format.Contains("bmp") || codec == "bmp") return "image/bmp";
        if (format.Contains("jxl") || codec == "jpegxl") return "image/jxl";

        if (format.Contains("mov,mp4") || format.Contains("mp4")) return "video/mp4";
        if (format.Contains("matroska") || format.Contains("mkv")) return "video/x-matroska";
        if (format.Contains("webm")) return "video/webm";
        if (format.Contains("avi")) return "video/x-msvideo";
        if (format.Contains("quicktime") || format.Contains("mov")) return "video/quicktime";

        return null;
    }
}
