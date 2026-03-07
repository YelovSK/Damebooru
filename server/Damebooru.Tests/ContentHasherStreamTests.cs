using Damebooru.Processing.Infrastructure;

namespace Damebooru.Tests;

public class ContentHasherStreamTests
{
    [Fact]
    public async Task ComputeContentHashAsync_StreamMatchesFilePathHash()
    {
        var bytes = Enumerable.Range(0, 200_000)
            .Select(i => (byte)(i % 251))
            .ToArray();
        var filePath = Path.GetTempFileName();

        try
        {
            await File.WriteAllBytesAsync(filePath, bytes);
            var hasher = new ContentHasher();

            var fileHash = await hasher.ComputeContentHashAsync(filePath);
            await using var stream = new MemoryStream(bytes, writable: false);
            var streamHash = await hasher.ComputeContentHashAsync(stream);

            Assert.Equal(fileHash, streamHash);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ComputeContentHashAsync_NonSeekableStreamMatchesSeekableStream()
    {
        var bytes = Enumerable.Range(0, 220_000)
            .Select(i => (byte)(i % 241))
            .ToArray();
        var hasher = new ContentHasher();

        await using var seekable = new MemoryStream(bytes, writable: false);
        await using var nonSeekable = new NonSeekableReadStream(bytes);

        var seekableHash = await hasher.ComputeContentHashAsync(seekable);
        var nonSeekableHash = await hasher.ComputeContentHashAsync(nonSeekable);

        Assert.Equal(seekableHash, nonSeekableHash);
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly byte[] _bytes;
        private int _position;

        public NonSeekableReadStream(byte[] bytes)
        {
            _bytes = bytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _bytes.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(count, remaining);
            Buffer.BlockCopy(_bytes, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _bytes.Length - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var toCopy = Math.Min(buffer.Length, remaining);
            _bytes.AsMemory(_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
