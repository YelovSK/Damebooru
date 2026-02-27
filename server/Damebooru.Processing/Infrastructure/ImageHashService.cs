// Portions of this file are derived from PdqHash:
// https://github.com/crispthinking/PdqHash/blob/main/src/PdqHash/PdqHasher.cs
// See THIRD_PARTY_NOTICES.md for license details.using Damebooru.Core.Interfaces;

using Damebooru.Core.Interfaces;
using PhotoSauce.MagicScaler;
using PhotoSauce.MagicScaler.Transforms;
using System.Drawing;
using System.Text;

namespace Damebooru.Processing.Infrastructure;

/// <summary>
/// Computes a 256-bit PDQ hash from MagicScaler-decoded grayscale pixels.
/// </summary>
public class ImageHashService : ISimilarityService
{
    private const int MaxDecodeDimension = 1024;
    private const int PdqReducedSize = 64;
    private const int PdqDctSize = 16;
    private const int PdqNumJaroszXyPasses = 2;
    private const int PdqJaroszWindowSizeDivisor = 128;

    private static readonly float DctMatrixScaleFactor = (float)Math.Sqrt(2.0 / PdqReducedSize);
    private static readonly float[,] DctMatrix = BuildDctMatrix();

    public ImageHashService()
    {
    }

    public async Task<SimilarityHashes> ComputeHashesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var (pixels, scaledWidth, scaledHeight) = await DecodeGrayscalePixelsAsync(filePath, cancellationToken);
        if (scaledWidth <= 0 || scaledHeight <= 0 || pixels.Length == 0)
        {
            throw new InvalidOperationException($"Failed to resolve dimensions for {filePath}");
        }

        var expectedPixelCount = scaledWidth * scaledHeight;
        if (pixels.Length < expectedPixelCount)
        {
            throw new InvalidOperationException($"Unexpected pixel count ({pixels.Length}) for {filePath}, expected {expectedPixelCount}");
        }

        var pdqHash = ComputePdqHash256Hex(pixels, scaledHeight, scaledWidth);
        return new SimilarityHashes(pdqHash);
    }

    private static Task<(byte[] Pixels, int Width, int Height)> DecodeGrayscalePixelsAsync(string filePath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imageInfo = ImageFileInfo.Load(filePath);
            if (imageInfo.Frames.Count == 0)
            {
                return ([], 0, 0);
            }

            var frame = imageInfo.Frames[0];
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                return ([], 0, 0);
            }

            var scale = Math.Min(1.0, Math.Min((double)MaxDecodeDimension / frame.Width, (double)MaxDecodeDimension / frame.Height));
            var requestedWidth = Math.Max(1, (int)Math.Round(frame.Width * scale));
            var requestedHeight = Math.Max(1, (int)Math.Round(frame.Height * scale));

            var settings = new ProcessImageSettings
            {
                Width = requestedWidth,
                Height = requestedHeight,
                ResizeMode = CropScaleMode.Max,
            };

            using var pipeline = MagicImageProcessor.BuildPipeline(filePath, settings);
            pipeline.AddTransform(new FormatConversionTransform(PixelFormats.Grey8bpp));

            var pixelSource = pipeline.PixelSource;
            var scaledWidth = pixelSource.Width;
            var scaledHeight = pixelSource.Height;
            var pixelCount = scaledWidth * scaledHeight;
            if (scaledWidth <= 0 || scaledHeight <= 0 || pixelCount <= 0)
            {
                return ([], 0, 0);
            }

            var pixels = new byte[pixelCount];
            pixelSource.CopyPixels(new Rectangle(0, 0, scaledWidth, scaledHeight), scaledWidth, pixels);

            return (pixels, scaledWidth, scaledHeight);
        }, cancellationToken);
    }

    private static string ComputePdqHash256Hex(byte[] pixels, int numRows, int numCols)
    {
        var pool = System.Buffers.ArrayPool<float>.Shared;
        var pixelCount = numRows * numCols;
        var reducedCount = PdqReducedSize * PdqReducedSize;
        var dctCount = PdqDctSize * PdqDctSize;

        float[] buffer1 = pool.Rent(pixelCount);
        float[] buffer2 = pool.Rent(pixelCount);
        float[] reduced64 = pool.Rent(reducedCount);
        float[] dct16 = pool.Rent(dctCount);

        try
        {
            FillFloatLumaFromGrayBuffer(pixels, buffer1, pixelCount);
            PdqHash256FromFloatLuma(buffer1, buffer2, numRows, numCols, reduced64, dct16);

            var median = Median(dct16, dctCount);
            var words = new ulong[4];

            for (int i = 0; i < dctCount; i++)
            {
                if (dct16[i] <= median) continue;
                SetBit(words, i);
            }

            var builder = new StringBuilder(64);
            for (int i = 0; i < words.Length; i++)
            {
                builder.Append(words[i].ToString("x16"));
            }

            return builder.ToString();
        }
        finally
        {
            pool.Return(buffer1);
            pool.Return(buffer2);
            pool.Return(reduced64);
            pool.Return(dct16);
        }
    }
    private static void FillFloatLumaFromGrayBuffer(byte[] gray, float[] luma, int length)
    {
        for (int i = 0; i < length; i++)
        {
            luma[i] = gray[i];
        }
    }

    private static void PdqHash256FromFloatLuma(
        float[] buffer1,
        float[] buffer2,
        int numRows,
        int numCols,
        float[] reduced64,
        float[] dct16)
    {
        var windowSizeAlongRows = ComputeJaroszFilterWindowSize(numCols);
        var windowSizeAlongCols = ComputeJaroszFilterWindowSize(numRows);

        JaroszFilterFloat(
            buffer1,
            buffer2,
            numRows,
            numCols,
            windowSizeAlongRows,
            windowSizeAlongCols,
            PdqNumJaroszXyPasses);

        DecimateFloat(buffer1, numRows, numCols, reduced64);
        Dct64To16(reduced64, dct16);
    }

    private static void Dct64To16(float[] input64, float[] output16)
    {
        var pool = System.Buffers.ArrayPool<float>.Shared;
        float[] temp = pool.Rent(PdqDctSize * PdqReducedSize);

        try
        {
            for (int i = 0; i < PdqDctSize; i++)
            {
                for (int j = 0; j < PdqReducedSize; j++)
                {
                    var sum = 0.0f;
                    for (int k = 0; k < PdqReducedSize; k++)
                    {
                        sum += DctMatrix[i, k] * input64[(k * PdqReducedSize) + j];
                    }
                    temp[(i * PdqReducedSize) + j] = sum;
                }
            }

            for (int i = 0; i < PdqDctSize; i++)
            {
                for (int j = 0; j < PdqDctSize; j++)
                {
                    var sum = 0.0f;
                    for (int k = 0; k < PdqReducedSize; k++)
                    {
                        sum += temp[(i * PdqReducedSize) + k] * DctMatrix[j, k];
                    }
                    output16[(i * PdqDctSize) + j] = sum;
                }
            }
        }
        finally
        {
            pool.Return(temp);
        }
    }

    private static void DecimateFloat(float[] input, int inNumRows, int inNumCols, float[] output)
    {
        for (int i = 0; i < PdqReducedSize; i++)
        {
            var ini = (int)((i + 0.5) * inNumRows / PdqReducedSize);
            if (ini >= inNumRows)
            {
                ini = inNumRows - 1;
            }

            for (int j = 0; j < PdqReducedSize; j++)
            {
                var inj = (int)((j + 0.5) * inNumCols / PdqReducedSize);
                if (inj >= inNumCols)
                {
                    inj = inNumCols - 1;
                }

                output[(i * PdqReducedSize) + j] = input[(ini * inNumCols) + inj];
            }
        }
    }

    private static void JaroszFilterFloat(
        float[] buffer1,
        float[] buffer2,
        int numRows,
        int numCols,
        int windowSizeAlongRows,
        int windowSizeAlongCols,
        int nreps)
    {
        for (int i = 0; i < nreps; i++)
        {
            BoxAlongRowsFloat(buffer1, buffer2, numRows, numCols, windowSizeAlongRows);
            BoxAlongColsFloat(buffer2, buffer1, numRows, numCols, windowSizeAlongCols);
        }
    }

    private static void BoxAlongColsFloat(float[] input, float[] output, int numRows, int numCols, int windowSize)
    {
        for (int j = 0; j < numCols; j++)
        {
            Box1DFloat(input, j, output, j, numRows, numCols, windowSize);
        }
    }

    private static void BoxAlongRowsFloat(float[] input, float[] output, int numRows, int numCols, int windowSize)
    {
        for (int i = 0; i < numRows; i++)
        {
            Box1DFloat(input, i * numCols, output, i * numCols, numCols, 1, windowSize);
        }
    }

    private static void Box1DFloat(
        float[] input,
        int inStartOffset,
        float[] output,
        int outStartOffset,
        int vectorLength,
        int stride,
        int fullWindowSize)
    {
        var halfWindowSize = (fullWindowSize + 2) / 2;
        var phase1Repeats = halfWindowSize - 1;
        var phase2Repeats = fullWindowSize - halfWindowSize + 1;
        var phase3Repeats = vectorLength - fullWindowSize;
        var phase4Repeats = halfWindowSize - 1;

        var leftIndex = 0;
        var rightIndex = 0;
        var outIndex = 0;
        var sum = 0.0f;
        var currentWindowSize = 0;

        for (int i = 0; i < phase1Repeats; i++)
        {
            sum += input[inStartOffset + rightIndex];
            currentWindowSize++;
            rightIndex += stride;
        }

        for (int i = 0; i < phase2Repeats; i++)
        {
            sum += input[inStartOffset + rightIndex];
            currentWindowSize++;
            output[outStartOffset + outIndex] = sum / currentWindowSize;
            rightIndex += stride;
            outIndex += stride;
        }

        for (int i = 0; i < phase3Repeats; i++)
        {
            sum += input[inStartOffset + rightIndex];
            sum -= input[inStartOffset + leftIndex];
            output[outStartOffset + outIndex] = sum / currentWindowSize;
            leftIndex += stride;
            rightIndex += stride;
            outIndex += stride;
        }

        for (int i = 0; i < phase4Repeats; i++)
        {
            sum -= input[inStartOffset + leftIndex];
            currentWindowSize--;
            output[outStartOffset + outIndex] = sum / currentWindowSize;
            leftIndex += stride;
            outIndex += stride;
        }
    }

    private static int ComputeJaroszFilterWindowSize(int dimensionSize)
    {
        return (dimensionSize + PdqJaroszWindowSizeDivisor - 1) / PdqJaroszWindowSizeDivisor;
    }

    private static float Median(float[] values, int length)
    {
        var pool = System.Buffers.ArrayPool<float>.Shared;
        float[] copy = pool.Rent(length);

        try
        {
            Array.Copy(values, copy, length);
            Array.Sort(copy, 0, length);
            return copy[length / 2];
        }
        finally
        {
            pool.Return(copy);
        }
    }

    private static void SetBit(ulong[] words, int bitIndex)
    {
        var wordIndex = bitIndex / 64;
        var bitInWord = 63 - (bitIndex % 64);
        words[wordIndex] |= 1UL << bitInWord;
    }

    private static float[,] BuildDctMatrix()
    {
        var matrix = new float[PdqDctSize, PdqReducedSize];

        for (int i = 0; i < PdqDctSize; i++)
        {
            for (int j = 0; j < PdqReducedSize; j++)
            {
                matrix[i, j] =
                    (float)(DctMatrixScaleFactor * Math.Cos((Math.PI / 2 / PdqReducedSize) * (i + 1) * (2 * j + 1)));
            }
        }

        return matrix;
    }
}
