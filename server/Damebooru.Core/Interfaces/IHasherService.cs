namespace Damebooru.Core.Interfaces;

public interface IHasherService
{
    /// <summary>
    /// Computes a fast partial content hash from a readable stream using the same semantics as the file-path overload.
    /// </summary>
    Task<string> ComputeContentHashAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a fast partial file hash using xxHash64 over the first and last 64KB + file size.
    /// Works identically for images and videos. Very fast on HDDs since it only reads ~128KB.
    /// </summary>
    Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken = default);
}
