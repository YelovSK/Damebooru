namespace Bakabooru.Core.Paths;

public static class MediaPaths
{
    public const string ThumbnailsRequestPath = "/thumbnails";

    public static string GetThumbnailUrl(string contentHash)
        => $"{ThumbnailsRequestPath}/{contentHash}.jpg";

    public static string GetPostContentUrl(int postId)
        => $"/api/posts/{postId}/content";
}
