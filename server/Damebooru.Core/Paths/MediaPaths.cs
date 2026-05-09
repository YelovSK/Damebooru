using Damebooru.Core.Config;

namespace Damebooru.Core.Paths;

public static class MediaPaths
{
    public const string GeneratedImageExtension = ".webp";
    public const string GeneratedImageGlobPattern = $"*{GeneratedImageExtension}";
    public const string GeneratedImageContentType = "image/webp";

    public const string ThumbnailContentType = GeneratedImageContentType;
    public const int ThumbnailSize = 200;
    public const string ThumbnailSizeSegment = "200";
    public const string ThumbnailsStorageFallbackPath = "data/thumbnails";
    public const string ThumbnailsRequestPath = "/thumbnails";

    public const string PreviewContentType = GeneratedImageContentType;
    public const int PreviewSize = 400;
    public const string PreviewsStorageFallbackPath = "data/previews";
    public const string PreviewsRequestPath = "/previews";

    public static string ResolvePreviewStoragePath(string contentRootPath, string? configuredPath)
        => StoragePathResolver.ResolvePath(contentRootPath, configuredPath, PreviewsStorageFallbackPath);

    public static string ResolveThumbnailStoragePath(string contentRootPath, string? configuredPath)
        => StoragePathResolver.ResolvePath(contentRootPath, configuredPath, ThumbnailsStorageFallbackPath);

    public static string GetGeneratedImageFileName(string contentHash)
        => $"{contentHash}{GeneratedImageExtension}";

    public static string GetPreviewRelativePath(int libraryId, string contentHash)
        => $"{libraryId}/{GetGeneratedImageFileName(contentHash)}";

    public static string GetPreviewFilePath(string previewRootPath, int libraryId, string contentHash)
        => Path.Combine(previewRootPath, libraryId.ToString(), GetGeneratedImageFileName(contentHash));

    public static string GetThumbnailRelativePath(int libraryId, string contentHash)
        => $"{ThumbnailSizeSegment}/{libraryId}/{GetGeneratedImageFileName(contentHash)}";

    public static string GetThumbnailFilePath(string thumbnailRootPath, int libraryId, string contentHash)
        => Path.Combine(thumbnailRootPath, ThumbnailSizeSegment, libraryId.ToString(), GetGeneratedImageFileName(contentHash));
}
