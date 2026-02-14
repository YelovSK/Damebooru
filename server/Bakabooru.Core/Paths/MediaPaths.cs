using Bakabooru.Core.Config;

namespace Bakabooru.Core.Paths;

public static class MediaPaths
{
    public const string ThumbnailExtension = ".webp";
    public const string ThumbnailGlobPattern = $"*{ThumbnailExtension}";
    public const string ThumbnailContentType = "image/webp";
    public const string ThumbnailsStorageFallbackPath = "../../data/thumbnails";
    public const string ThumbnailsRequestPath = "/thumbnails";

    public static string ResolveThumbnailStoragePath(string contentRootPath, string? configuredPath)
        => StoragePathResolver.ResolvePath(contentRootPath, configuredPath, ThumbnailsStorageFallbackPath);

    public static string GetThumbnailFileName(string contentHash)
        => $"{contentHash}{ThumbnailExtension}";

    public static string GetThumbnailFilePath(string thumbnailRootPath, string contentHash)
        => Path.Combine(thumbnailRootPath, GetThumbnailFileName(contentHash));

    public static string GetThumbnailUrl(string contentHash)
        => $"{ThumbnailsRequestPath}/{GetThumbnailFileName(contentHash)}";

    public static string GetPostContentUrl(int postId)
        => $"/api/posts/{postId}/content";
}
