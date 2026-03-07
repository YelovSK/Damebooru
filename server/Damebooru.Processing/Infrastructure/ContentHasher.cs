using Damebooru.Core.Interfaces;
using System.IO.Hashing;

namespace Damebooru.Processing.Infrastructure;

public class ContentHasher : IHasherService
{
    private const int ContentHashChunkSize = 64 * 1024; // 64 KB

    public async Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: ContentHashChunkSize, useAsync: true);
        return await ComputeContentHashAsync(stream, cancellationToken);
    }

    public async Task<string> ComputeContentHashAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("Stream must be readable.");
        }

        return stream.CanSeek
            ? await ComputeContentHashSeekableAsync(stream, cancellationToken)
            : await ComputeContentHashStreamingAsync(stream, cancellationToken);
    }

    private static async Task<string> ComputeContentHashSeekableAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        var fileSize = stream.Length;
        var hasher = new XxHash64();

        Span<byte> sizeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sizeBytes, fileSize);
        hasher.Append(sizeBytes);

        var buffer = new byte[ContentHashChunkSize];
        var firstRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (firstRead > 0)
        {
            hasher.Append(buffer.AsSpan(0, firstRead));
        }

        if (fileSize > ContentHashChunkSize * 2)
        {
            stream.Seek(-ContentHashChunkSize, SeekOrigin.End);
            var lastRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (lastRead > 0)
            {
                hasher.Append(buffer.AsSpan(0, lastRead));
            }
        }

        var hash = hasher.GetCurrentHashAsUInt64();
        return hash.ToString("x16");
    }

    private static async Task<string> ComputeContentHashStreamingAsync(Stream stream, CancellationToken cancellationToken)
    {
        var firstChunk = new byte[ContentHashChunkSize];
        var ringBuffer = new byte[ContentHashChunkSize];
        var readBuffer = new byte[81920];
        var firstChunkCount = 0;
        long totalBytes = 0;

        while (true)
        {
            var read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (firstChunkCount < ContentHashChunkSize)
            {
                var toCopy = Math.Min(ContentHashChunkSize - firstChunkCount, read);
                Buffer.BlockCopy(readBuffer, 0, firstChunk, firstChunkCount, toCopy);
                firstChunkCount += toCopy;
            }

            for (var i = 0; i < read; i++)
            {
                ringBuffer[(int)(totalBytes % ContentHashChunkSize)] = readBuffer[i];
                totalBytes++;
            }
        }

        var hasher = new XxHash64();
        Span<byte> sizeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sizeBytes, totalBytes);
        hasher.Append(sizeBytes);

        if (firstChunkCount > 0)
        {
            hasher.Append(firstChunk.AsSpan(0, firstChunkCount));
        }

        if (totalBytes > ContentHashChunkSize * 2)
        {
            var orderedTail = new byte[ContentHashChunkSize];
            var startIndex = (int)(totalBytes % ContentHashChunkSize);
            for (var i = 0; i < ContentHashChunkSize; i++)
            {
                orderedTail[i] = ringBuffer[(startIndex + i) % ContentHashChunkSize];
            }

            hasher.Append(orderedTail);
        }

        return hasher.GetCurrentHashAsUInt64().ToString("x16");
    }
}
