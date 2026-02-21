using Damebooru.Core;
using Damebooru.Core.Interfaces;
using FFMpegCore;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Infrastructure;

/// <summary>
/// Unified media processor using FFmpeg for all formats (images, videos, JXL, AVIF, WebP, etc.)
/// </summary>
public class FFmpegProcessor : IMediaFileProcessor
{
    private readonly ILogger<FFmpegProcessor> _logger;
    private static readonly TimeSpan MinVideoCaptureTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxVideoCaptureTime = TimeSpan.FromSeconds(10);

    public FFmpegProcessor(ILogger<FFmpegProcessor> logger)
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

            var analysis = await FFProbe.AnalyseAsync(sourcePath, null, cancellationToken);
            var isVideo = IsVideo(analysis);
            var duration = analysis.Duration;

            var arguments = isVideo
                // Video — avoid the first frame (often black/fade-in/logo).
                ? FFMpegArguments.FromFileInput(sourcePath, true, options => options.Seek(GetVideoCaptureTime(duration)))
                // Image — convert first frame.
                : FFMpegArguments.FromFileInput(sourcePath);

            await arguments
                .OutputToFile(destinationPath, true, options => options
                    .WithCustomArgument($"-vf \"scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease\"")
                    .WithCustomArgument("-frames:v 1")
                )
                .ProcessAsynchronously();

             EnsureThumbnailCreated(destinationPath, sourcePath);
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
                $"FFmpeg completed but did not create thumbnail. Source: '{sourcePath}', Destination: '{destinationPath}'.");
        }

        var size = new FileInfo(destinationPath).Length;
        if (size <= 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg created empty thumbnail file. Source: '{sourcePath}', Destination: '{destinationPath}'.");
        }
    }

    public async Task<MediaMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
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

    private static bool IsVideo(IMediaAnalysis analysis)
    {
        if (analysis.PrimaryVideoStream == null)
        {
            return false;
        }

        var format = (analysis.Format.FormatName ?? "").ToLowerInvariant();
        
        // Exclude image formats that might have video streams
        if (format.Contains("jpeg") || format.Contains("jxl") || 
            format.Contains("png") || format.Contains("gif") || 
            format.Contains("webp") || format.Contains("bmp"))
        {
            return false;
        }
        
        // Additional check: duration should be meaningful for actual videos
        return analysis.Duration > TimeSpan.FromMilliseconds(100);
    }
}
