using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using Damebooru.Core.Config;
using Damebooru.Processing;
using Damebooru.Processing.Infrastructure.External.Shared;
using Microsoft.Extensions.DependencyInjection;
using PhotoSauce.MagicScaler;

namespace Damebooru.Tests;

public sealed class ImageUploadPreparerTests
{
    private static readonly ImageUploadPreparationOptions Options = new()
    {
        MaxUploadBytes = 10L * 1024 * 1024,
        MaxDimension = 20,
        SupportedUploadContentTypes = new HashSet<string> { "image/png" },
        SupportedUploadExtensions = new HashSet<string> { ".png" },
    };

    static ImageUploadPreparerTests()
    {
        // Registers the image codecs the preparer relies on.
        new ServiceCollection().AddDamebooruProcessing(new DamebooruConfig());
    }

    [Fact]
    public async Task PrepareAsync_ImageOverMaxDimension_IsDownscaledEvenWhenSmallInBytes()
    {
        await using var png = CreatePng(width: 10, height: 60);

        await using var prepared = await ImageUploadPreparer.PrepareAsync(png, "tall.png", "image/png", Options, CancellationToken.None);

        Assert.Equal("image/jpeg", prepared.ContentType);
        var frame = ImageFileInfo.Load(prepared.Stream).Frames[0];
        Assert.True(Math.Max(frame.Width, frame.Height) <= 20, $"{frame.Width}x{frame.Height}");
    }

    [Fact]
    public async Task PrepareAsync_ImageWithinLimits_IsUploadedUnchanged()
    {
        await using var png = CreatePng(width: 10, height: 10);

        await using var prepared = await ImageUploadPreparer.PrepareAsync(png, "small.png", "image/png", Options, CancellationToken.None);

        Assert.Equal("image/png", prepared.ContentType);
        Assert.Same(png, prepared.Stream);
    }

    [Fact]
    public async Task PrepareAsync_UnsupportedFormat_IsConvertedWithoutUpscaling()
    {
        await using var png = CreatePng(width: 10, height: 12);
        var pngOnlyAsJpeg = new ImageUploadPreparationOptions
        {
            SupportedUploadContentTypes = new HashSet<string> { "image/jpeg" },
            SupportedUploadExtensions = new HashSet<string> { ".jpg" },
        };

        await using var prepared = await ImageUploadPreparer.PrepareAsync(png, "small.png", "image/png", pngOnlyAsJpeg, CancellationToken.None);

        Assert.Equal("image/jpeg", prepared.ContentType);
        var frame = ImageFileInfo.Load(prepared.Stream).Frames[0];
        Assert.Equal((10, 12), (frame.Width, frame.Height));
    }

    private static MemoryStream CreatePng(int width, int height)
    {
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                raw[y * (width + 1) + 1 + x] = (byte)(x * 20 + y * 3);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // 8-bit grayscale

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        png.Position = 0;
        return png;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typeAndData);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(typeAndData));
        output.Write(crc);
    }
}
