using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Services;

/// <summary>
/// A post's content, read from any one of its files; all of them hold the same bytes.
/// </summary>
public sealed record PostEnrichmentTarget(
    int PostId,
    string ContentHash,
    string FullPath);

public sealed record PostMetadataResult(
    int PostId,
    int Width,
    int Height);

public sealed record PostSimilarityResult(
    int PostId,
    string PdqHash256);

public class MediaEnrichmentService
{
    private readonly IMediaFileProcessor _mediaFileProcessor;
    private readonly ISimilarityService _similarityService;
    private readonly string _previewPath;
    private readonly string _thumbnailPath;

    public MediaEnrichmentService(
        IMediaFileProcessor mediaFileProcessor,
        ISimilarityService similarityService,
        IOptions<DamebooruConfig> options,
        IHostEnvironment hostEnvironment)
    {
        _mediaFileProcessor = mediaFileProcessor;
        _similarityService = similarityService;
        _previewPath = MediaPaths.ResolvePreviewStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.PreviewPath);
        _thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath);

        if (!Directory.Exists(_previewPath))
        {
            Directory.CreateDirectory(_previewPath);
        }

        if (!Directory.Exists(_thumbnailPath))
        {
            Directory.CreateDirectory(_thumbnailPath);
        }
    }

    public bool HasThumbnail(PostEnrichmentTarget target)
        => File.Exists(GetThumbnailPath(target));

    public bool HasPreview(PostEnrichmentTarget target)
        => File.Exists(GetPreviewPath(target));

    public bool HasGeneratedImages(PostEnrichmentTarget target)
        => HasPreview(target) && HasThumbnail(target);

    public async Task GenerateGeneratedImagesAsync(PostEnrichmentTarget target, CancellationToken cancellationToken)
    {
        await GeneratePreviewAsync(target, cancellationToken);
        await GenerateThumbnailAsync(target, cancellationToken);
    }

    public Task GeneratePreviewAsync(PostEnrichmentTarget target, CancellationToken cancellationToken)
        => _mediaFileProcessor.GeneratePreviewAsync(
            target.FullPath,
            GetPreviewPath(target),
            MediaPaths.PreviewSize,
            cancellationToken);

    public Task GenerateThumbnailAsync(PostEnrichmentTarget target, CancellationToken cancellationToken)
        => _mediaFileProcessor.GenerateThumbnailAsync(
            target.FullPath,
            GetThumbnailPath(target),
            MediaPaths.ThumbnailSize,
            cancellationToken);

    public async Task<PostMetadataResult> ExtractMetadataAsync(PostEnrichmentTarget target, CancellationToken cancellationToken)
    {
        var metadata = await _mediaFileProcessor.GetMetadataAsync(target.FullPath, cancellationToken);
        if (metadata.Width <= 0 || metadata.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Metadata extraction produced invalid dimensions for post {target.PostId}: {target.FullPath} ({metadata.Width}x{metadata.Height})");
        }

        return new PostMetadataResult(target.PostId, metadata.Width, metadata.Height);
    }

    public async Task<PostSimilarityResult?> ComputeSimilarityAsync(PostEnrichmentTarget target, CancellationToken cancellationToken)
    {
        if (!SupportedMedia.IsImage(Path.GetExtension(target.FullPath)))
        {
            return null;
        }

        var hashes = await _similarityService.ComputeHashesAsync(target.FullPath, cancellationToken);
        return new PostSimilarityResult(target.PostId, hashes.PdqHash256);
    }

    private string GetPreviewPath(PostEnrichmentTarget target)
        => MediaPaths.GetPreviewFilePath(_previewPath, target.ContentHash);

    private string GetThumbnailPath(PostEnrichmentTarget target)
        => MediaPaths.GetThumbnailFilePath(_thumbnailPath, target.ContentHash);
}
