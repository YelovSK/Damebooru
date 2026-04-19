using Damebooru.Core.Entities;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
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

    public async Task SyncPostFolderTagsAsync(
        DamebooruDbContext dbContext,
        IReadOnlyCollection<int> postIds,
        CancellationToken cancellationToken = default)
    {
        if (postIds.Count == 0)
        {
            return;
        }

        var normalizedPostIds = postIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (normalizedPostIds.Count == 0)
        {
            return;
        }

        var postFiles = await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => normalizedPostIds.Contains(pf.PostId))
            .Select(pf => new { pf.PostId, pf.RelativePath })
            .ToListAsync(cancellationToken);

        var existingFolderLinks = await dbContext.PostTags
            .Where(pt => normalizedPostIds.Contains(pt.PostId) && pt.Source == PostTagSource.Folder)
            .Select(pt => new { pt.PostId, pt.TagId, TagName = pt.Tag.Name })
            .ToListAsync(cancellationToken);

        var existingByPost = existingFolderLinks
            .GroupBy(link => link.PostId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var pathsByPost = postFiles
            .GroupBy(pf => pf.PostId)
            .ToDictionary(group => group.Key, group => group.Select(pf => pf.RelativePath).ToList());

        var tagsToAddByPost = new Dictionary<int, List<string>>();
        var tagLinksToRemove = new List<PostTag>();
        var neededTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var postId in normalizedPostIds)
        {
            var desiredFolderTags = (pathsByPost.TryGetValue(postId, out var relativePaths)
                    ? relativePaths.SelectMany(GetFolderTags)
                    : [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingFolderTags = existingByPost.TryGetValue(postId, out var existingLinks)
                ? existingLinks
                : [];
            var existingFolderNames = existingFolderTags
                .Select(link => link.TagName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tagsToAdd = desiredFolderTags
                .Where(tagName => !existingFolderNames.Contains(tagName))
                .ToList();
            var tagIdsToRemove = existingFolderTags
                .Where(link => !desiredFolderTags.Contains(link.TagName))
                .Select(link => link.TagId)
                .Distinct()
                .ToList();

            if (tagsToAdd.Count > 0)
            {
                tagsToAddByPost[postId] = tagsToAdd;
                foreach (var tagName in tagsToAdd)
                {
                    neededTagNames.Add(tagName);
                }
            }

            foreach (var tagId in tagIdsToRemove)
            {
                tagLinksToRemove.Add(new PostTag
                {
                    PostId = postId,
                    TagId = tagId,
                    Source = PostTagSource.Folder,
                });
            }
        }

        if (tagLinksToRemove.Count > 0)
        {
            dbContext.PostTags.RemoveRange(tagLinksToRemove);
        }

        if (neededTagNames.Count == 0 && tagLinksToRemove.Count == 0)
        {
            return;
        }

        var tagsByName = await dbContext.Tags
            .Where(t => neededTagNames.Contains(t.Name))
            .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var tagName in neededTagNames)
        {
            if (tagsByName.ContainsKey(tagName))
            {
                continue;
            }

            var tag = new Tag { Name = tagName };
            dbContext.Tags.Add(tag);
            tagsByName[tagName] = tag;
        }

        foreach (var (postId, tagNames) in tagsToAddByPost)
        {
            foreach (var tagName in tagNames)
            {
                dbContext.PostTags.Add(new PostTag
                {
                    PostId = postId,
                    Tag = tagsByName[tagName],
                    Source = PostTagSource.Folder,
                });
            }
        }
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
