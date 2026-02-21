using System.Text.RegularExpressions;

namespace Damebooru.Processing.Services;

public sealed class FolderTaggingPlan
{
    public IReadOnlyList<string> FolderTags { get; init; } = [];
    public IReadOnlyList<string> TagsToAdd { get; init; } = [];
}

public class FolderTaggingService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public FolderTaggingPlan BuildPlan(string relativePath, IEnumerable<string>? existingTagNames = null)
    {
        var folderTags = GetFolderTags(relativePath);
        if (folderTags.Count == 0)
        {
            return new FolderTaggingPlan
            {
                FolderTags = [],
                TagsToAdd = []
            };
        }

        var existing = (existingTagNames ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tagsToAdd = folderTags
            .Where(tag => !existing.Contains(tag))
            .ToList();

        return new FolderTaggingPlan
        {
            FolderTags = folderTags,
            TagsToAdd = tagsToAdd
        };
    }

    private static List<string> GetFolderTags(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return [];
        }

        var normalizedPath = relativePath.Trim().Replace('\\', '/');
        var lastSeparator = normalizedPath.LastIndexOf('/');
        if (lastSeparator <= 0)
        {
            return [];
        }

        var directoryPart = normalizedPath[..lastSeparator];
        var segments = directoryPart
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<string>(segments.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            var normalizedTag = NormalizeSegment(segment);
            if (string.IsNullOrEmpty(normalizedTag))
            {
                continue;
            }

            if (normalizedTag.Length > 100)
            {
                normalizedTag = normalizedTag[..100];
            }

            if (seen.Add(normalizedTag))
            {
                result.Add(normalizedTag);
            }
        }

        return result;
    }

    private static string NormalizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var collapsedWhitespace = WhitespaceRegex.Replace(segment.Trim(), "_");
        return collapsedWhitespace.ToLowerInvariant();
    }
}
