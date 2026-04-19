using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Services;

public sealed record PostFileEnrichmentTarget(
    int PostFileId,
    int LibraryId,
    string ContentHash,
    string RelativePath,
    string LibraryPath);

public sealed record PostFileMetadataResult(
    int PostFileId,
    int Width,
    int Height,
    string ContentType);

public sealed record PostFileSimilarityResult(
    int PostFileId,
    string PdqHash256);

public class MediaEnrichmentService
{
    private const int ThumbnailSize = 400;

    private readonly IMediaFileProcessor _mediaFileProcessor;
    private readonly ISimilarityService _similarityService;
    private readonly string _thumbnailPath;

    public MediaEnrichmentService(
        IMediaFileProcessor mediaFileProcessor,
        ISimilarityService similarityService,
        IOptions<DamebooruConfig> options,
        IHostEnvironment hostEnvironment)
    {
        _mediaFileProcessor = mediaFileProcessor;
        _similarityService = similarityService;
        _thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath);

        if (!Directory.Exists(_thumbnailPath))
        {
            Directory.CreateDirectory(_thumbnailPath);
        }
    }

    public bool HasThumbnail(PostFileEnrichmentTarget target)
        => File.Exists(GetThumbnailPath(target));

    public Task GenerateThumbnailAsync(PostFileEnrichmentTarget target, CancellationToken cancellationToken)
        => _mediaFileProcessor.GenerateThumbnailAsync(GetFullPath(target), GetThumbnailPath(target), ThumbnailSize, cancellationToken);

    public async Task<PostFileMetadataResult> ExtractMetadataAsync(PostFileEnrichmentTarget target, CancellationToken cancellationToken)
    {
        var metadata = await _mediaFileProcessor.GetMetadataAsync(GetFullPath(target), cancellationToken);
        if (metadata.Width <= 0 || metadata.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Metadata extraction produced invalid dimensions for post file {target.PostFileId}: {target.RelativePath} ({metadata.Width}x{metadata.Height})");
        }

        return new PostFileMetadataResult(
            target.PostFileId,
            metadata.Width,
            metadata.Height,
            SupportedMedia.GetMimeType(Path.GetExtension(target.RelativePath)));
    }

    public async Task<PostFileSimilarityResult?> ComputeSimilarityAsync(PostFileEnrichmentTarget target, CancellationToken cancellationToken)
    {
        if (!SupportedMedia.IsImage(Path.GetExtension(target.RelativePath)))
        {
            return null;
        }

        var hashes = await _similarityService.ComputeHashesAsync(GetFullPath(target), cancellationToken);
        return new PostFileSimilarityResult(target.PostFileId, hashes.PdqHash256);
    }

    private string GetThumbnailPath(PostFileEnrichmentTarget target)
        => MediaPaths.GetThumbnailFilePath(_thumbnailPath, target.LibraryId, target.ContentHash);

    private static string GetFullPath(PostFileEnrichmentTarget target)
        => Path.Combine(target.LibraryPath, target.RelativePath);
}
