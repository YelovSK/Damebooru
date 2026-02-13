namespace Bakabooru.Core.Paths;

public static class RelativePathMatcher
{
    public static string NormalizePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Trim().Replace('\\', '/').Trim('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized;
    }

    public static bool IsWithinPrefix(string relativePath, string normalizedPrefix)
    {
        var normalizedPath = NormalizePath(relativePath);
        if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(normalizedPrefix))
        {
            return false;
        }

        return normalizedPath.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith($"{normalizedPrefix}/", StringComparison.OrdinalIgnoreCase);
    }
}
