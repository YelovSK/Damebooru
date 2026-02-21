using Damebooru.Core.Interfaces;
using System.IO.Hashing;

namespace Damebooru.Processing.Infrastructure;

public class ContentHasher : IHasherService
{
    private const int ContentHashChunkSize = 64 * 1024; // 64 KB

    public async Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        var fileSize = fileInfo.Length;

        var hasher = new XxHash64();

        // Feed file size as discriminator
        Span<byte> sizeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sizeBytes, fileSize);
        hasher.Append(sizeBytes);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 
            bufferSize: ContentHashChunkSize, useAsync: true);

        var buffer = new byte[ContentHashChunkSize];

        // Read first chunk
        var firstRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (firstRead > 0)
            hasher.Append(buffer.AsSpan(0, firstRead));

        // Read last chunk (if file is large enough that it doesn't overlap)
        if (fileSize > ContentHashChunkSize * 2)
        {
            stream.Seek(-ContentHashChunkSize, SeekOrigin.End);
            var lastRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (lastRead > 0)
                hasher.Append(buffer.AsSpan(0, lastRead));
        }

        var hash = hasher.GetCurrentHashAsUInt64();
        return hash.ToString("x16");
    }
}
