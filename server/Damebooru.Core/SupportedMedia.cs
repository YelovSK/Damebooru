namespace Damebooru.Core;

/// <summary>
/// Centralized registry of supported media formats, extensions, and MIME types.
/// </summary>
public static class SupportedMedia
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tga", ".webp", ".jxl"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".avi", ".mov"
    };

    /// <summary>
    /// All supported file extensions (images + videos + special formats).
    /// </summary>
    public static readonly HashSet<string> AllExtensions = new(
        ImageExtensions.Concat(VideoExtensions),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps file extensions to MIME content types.
    /// </summary>
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".tga"] = "image/tga",
        [".webp"] = "image/webp",
        [".jxl"] = "image/jxl",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
    };

    public static bool IsImage(string extension) => ImageExtensions.Contains(extension);

    public static bool IsVideo(string extension) => VideoExtensions.Contains(extension);

    public static bool IsSupported(string extension) => AllExtensions.Contains(extension);

    public static string GetMimeType(string extension)
        => MimeMap.GetValueOrDefault(extension, "application/octet-stream");
}
