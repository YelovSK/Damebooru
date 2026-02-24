namespace Damebooru.Processing.Services.Duplicates;

internal static class DuplicatePathHelper
{
    public static string GetParentFolderPath(string relativePath)
    {
        var normalizedPath = NormalizePath(relativePath);
        var slashIndex = normalizedPath.LastIndexOf('/');
        return slashIndex < 0 ? string.Empty : normalizedPath[..slashIndex];
    }

    public static string NormalizeFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }

        return NormalizePath(folderPath);
    }

    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        normalized = normalized.Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }
}
