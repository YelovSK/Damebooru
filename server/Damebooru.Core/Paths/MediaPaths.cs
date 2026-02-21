using Damebooru.Core.Config;

namespace Damebooru.Core.Paths;

public static class MediaPaths
{
    public const string ThumbnailExtension = ".webp";
    public const string ThumbnailGlobPattern = $"*{ThumbnailExtension}";
    public const string ThumbnailContentType = "image/webp";
    public const string ThumbnailsStorageFallbackPath = "data/thumbnails";
    public const string ThumbnailsRequestPath = "/thumbnails";

    public static string ResolveThumbnailStoragePath(string contentRootPath, string? configuredPath)
        => StoragePathResolver.ResolvePath(contentRootPath, configuredPath, ThumbnailsStorageFallbackPath);

    public static string GetThumbnailFileName(string contentHash)
        => $"{contentHash}{ThumbnailExtension}";

    public static string GetThumbnailRelativePath(int libraryId, string contentHash)
        => $"{libraryId}/{GetThumbnailFileName(contentHash)}";

    public static string GetThumbnailFilePath(string thumbnailRootPath, int libraryId, string contentHash)
        => Path.Combine(thumbnailRootPath, libraryId.ToString(), GetThumbnailFileName(contentHash));
}
