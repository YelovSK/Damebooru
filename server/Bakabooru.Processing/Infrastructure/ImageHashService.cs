using Bakabooru.Core.Interfaces;
using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;

namespace Bakabooru.Processing.Infrastructure;

/// <summary>
/// Computes perceptual hashes (dHash + pHash) using FFmpeg for image decoding.
/// Supports all formats FFmpeg can decode (including JXL, AVIF, WebP, etc.)
/// </summary>
public class ImageHashService : ISimilarityService
{
    private const int DecodeSize = 32;
    private const int PHashLowFrequencySize = 8;
    private static readonly double[] PHashAlpha = BuildAlphaFactors(PHashLowFrequencySize);
    private static readonly double[,] PHashCos = BuildCosTable(PHashLowFrequencySize, DecodeSize);
    private readonly ILogger<ImageHashService> _logger;

    public ImageHashService(ILogger<ImageHashService> logger)
    {
        _logger = logger;
    }

    public async Task<SimilarityHashes?> ComputeHashesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var analysis = await FFProbe.AnalyseAsync(filePath, null, cancellationToken);
            var seekTime = GetVideoCaptureTime(analysis);

            // Decode once to a small grayscale surface, then derive multiple perceptual hashes from it.
            using var memoryStream = new MemoryStream();

            var arguments = seekTime.HasValue
                ? FFMpegArguments.FromFileInput(filePath, true, options => options.Seek(seekTime.Value))
                : FFMpegArguments.FromFileInput(filePath);

            await arguments
                .OutputToPipe(new StreamPipeSink(memoryStream), options => options
                    .WithVideoFilters(filter => filter.Scale(DecodeSize, DecodeSize))
                    .ForceFormat("rawvideo")
                    .WithCustomArgument("-pix_fmt gray")
                    .WithCustomArgument("-frames:v 1")
                )
                .ProcessAsynchronously();

            var pixels = memoryStream.ToArray();

            var expectedPixelCount = DecodeSize * DecodeSize;
            if (pixels.Length < expectedPixelCount)
            {
                _logger.LogWarning(
                    "Unexpected pixel count ({Count}) for {Path}, expected {Expected}",
                    pixels.Length,
                    filePath,
                    expectedPixelCount);
                return null;
            }

            var dHash = ComputeDHash64(pixels);
            var pHash = ComputePHash64(pixels);

            return new SimilarityHashes(dHash, pHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute perceptual hash for {Path}", filePath);
            return null;
        }
    }

    private static TimeSpan? GetVideoCaptureTime(IMediaAnalysis analysis)
    {
        if (!IsVideo(analysis))
        {
            return null;
        }

        var duration = analysis.Duration;
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
        // Keep it consistent with FFmpegProcessor thumbnail capture.
        var preferred = TimeSpan.FromTicks((long)(duration.Ticks * 0.2));
        var clamped = preferred < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : preferred > TimeSpan.FromSeconds(10)
                ? TimeSpan.FromSeconds(10)
                : preferred;

        return clamped > safeUpperBound ? safeUpperBound : clamped;
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

    private static ulong ComputeDHash64(byte[] pixels)
    {
        // Average downsample to 9x8 to preserve global structure from the full 32x32 surface.
        var reduced = DownsampleAverage(pixels, DecodeSize, DecodeSize, 9, 8);

        ulong hash = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int idx = y * 9 + x;
                if (reduced[idx] > reduced[idx + 1])
                {
                    hash |= 1UL << ((y * 8) + x);
                }
            }
        }

        return hash;
    }

    private static ulong ComputePHash64(byte[] pixels)
    {
        const int n = DecodeSize;
        var low = new double[PHashLowFrequencySize * PHashLowFrequencySize];

        for (int u = 0; u < PHashLowFrequencySize; u++)
        {
            for (int v = 0; v < PHashLowFrequencySize; v++)
            {
                double sum = 0;

                for (int x = 0; x < n; x++)
                {
                    var basisX = PHashCos[u, x];
                    for (int y = 0; y < n; y++)
                    {
                        var value = pixels[(y * n) + x];
                        sum += value * basisX * PHashCos[v, y];
                    }
                }

                low[(u * PHashLowFrequencySize) + v] = 0.25 * PHashAlpha[u] * PHashAlpha[v] * sum;
            }
        }

        // Ignore DC coefficient for threshold calculation.
        var thresholdValues = new double[low.Length - 1];
        Array.Copy(low, 1, thresholdValues, 0, thresholdValues.Length);
        Array.Sort(thresholdValues);
        var median = thresholdValues[thresholdValues.Length / 2];

        ulong hash = 0;
        for (int i = 0; i < low.Length; i++)
        {
            if (low[i] > median)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    private static byte[] DownsampleAverage(byte[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var dst = new byte[dstWidth * dstHeight];

        for (int dy = 0; dy < dstHeight; dy++)
        {
            int y0 = (dy * srcHeight) / dstHeight;
            int y1 = ((dy + 1) * srcHeight) / dstHeight;
            if (y1 <= y0) y1 = y0 + 1;

            for (int dx = 0; dx < dstWidth; dx++)
            {
                int x0 = (dx * srcWidth) / dstWidth;
                int x1 = ((dx + 1) * srcWidth) / dstWidth;
                if (x1 <= x0) x1 = x0 + 1;

                int sum = 0;
                int count = 0;

                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        sum += src[(y * srcWidth) + x];
                        count++;
                    }
                }

                dst[(dy * dstWidth) + dx] = (byte)(sum / Math.Max(1, count));
            }
        }

        return dst;
    }

    private static double[] BuildAlphaFactors(int size)
    {
        var alpha = new double[size];
        alpha[0] = 1.0 / Math.Sqrt(2.0);

        for (int i = 1; i < size; i++)
        {
            alpha[i] = 1.0;
        }

        return alpha;
    }

    private static double[,] BuildCosTable(int frequencyCount, int sampleCount)
    {
        var table = new double[frequencyCount, sampleCount];

        for (int frequency = 0; frequency < frequencyCount; frequency++)
        {
            for (int sample = 0; sample < sampleCount; sample++)
            {
                table[frequency, sample] = Math.Cos(((2 * sample + 1) * frequency * Math.PI) / (2 * sampleCount));
            }
        }

        return table;
    }
}
