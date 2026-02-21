namespace Damebooru.Core.Interfaces;

public interface IMediaFileProcessor
{
    Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int maxSize, CancellationToken cancellationToken = default);
    Task<MediaMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default);
}

public class MediaMetadata
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = string.Empty;
}
