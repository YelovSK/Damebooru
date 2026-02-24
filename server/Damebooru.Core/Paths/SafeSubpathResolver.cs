namespace Damebooru.Core.Paths;

public static class SafeSubpathResolver
{
    public static bool TryResolve(string basePath, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        var absoluteBasePath = Path.GetFullPath(basePath);
        var baseRoot = EnsureTrailingSeparator(absoluteBasePath);
        var candidate = Path.GetFullPath(Path.Combine(absoluteBasePath, relativePath ?? string.Empty));

        if (!candidate.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
