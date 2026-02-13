using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bakabooru.Core.Entities;

namespace Bakabooru.Core.Interfaces;

public interface IImageProcessor
{
    Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int width, int height, CancellationToken cancellationToken = default);
    Task<ImageMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default);
}

public class ImageMetadata
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = string.Empty;
    public string? ContentType { get; set; }
}
