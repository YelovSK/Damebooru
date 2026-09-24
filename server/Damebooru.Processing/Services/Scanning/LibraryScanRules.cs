using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Scanning;

/// <summary>
/// Which files of a library must not be indexed: ignored folders, and excluded files as long as their content is unchanged.
/// </summary>
internal sealed class LibraryScanRules
{
    private readonly List<string> _ignoredPrefixes;
    private readonly Dictionary<string, string> _excludedHashesByPath;

    private LibraryScanRules(List<string> ignoredPrefixes, Dictionary<string, string> excludedHashesByPath)
    {
        _ignoredPrefixes = ignoredPrefixes;
        _excludedHashesByPath = excludedHashesByPath;
    }

    public static async Task<LibraryScanRules> LoadAsync(DamebooruDbContext dbContext, int libraryId, CancellationToken cancellationToken)
    {
        var ignoredPrefixes = (await dbContext.LibraryIgnoredPaths
            .AsNoTracking()
            .Where(p => p.LibraryId == libraryId)
            .Select(p => p.RelativePathPrefix)
            .ToListAsync(cancellationToken))
            .Select(RelativePathMatcher.NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var excludedHashesByPath = (await dbContext.ExcludedFiles
            .AsNoTracking()
            .Where(e => e.LibraryId == libraryId)
            .Select(e => new { e.RelativePath, e.ContentHash })
            .ToListAsync(cancellationToken))
            .Where(e => !string.IsNullOrWhiteSpace(e.ContentHash))
            .ToDictionary(e => e.RelativePath, e => e.ContentHash, StringComparer.OrdinalIgnoreCase);

        return new LibraryScanRules(ignoredPrefixes, excludedHashesByPath);
    }

    public bool IsIgnored(string relativePath)
        => _ignoredPrefixes.Any(prefix => RelativePathMatcher.IsWithinPrefix(relativePath, prefix));

    public bool HasExclusion(string relativePath)
        => _excludedHashesByPath.ContainsKey(relativePath);

    public bool IsExcluded(string relativePath, string hash)
        => _excludedHashesByPath.TryGetValue(relativePath, out var excludedHash)
            && string.Equals(excludedHash, hash, StringComparison.OrdinalIgnoreCase);
}
