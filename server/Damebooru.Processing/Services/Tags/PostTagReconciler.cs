using Damebooru.Core.Entities;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

internal sealed record PostTagReconciliationTarget(string Name, TagCategoryKind Category);

internal sealed record PostTagReconciliationResult(int AddedTags, int RemovedTags, int UpdatedTagCategories);

internal static class PostTagReconciler
{
    public static async Task<PostTagReconciliationResult> ReconcileAsync(
        DamebooruDbContext db,
        Post post,
        PostTagSource source,
        IEnumerable<PostTagReconciliationTarget> desiredTags,
        CancellationToken cancellationToken)
    {
        var desired = desiredTags
            .Select(tag => new PostTagReconciliationTarget(Tag.NormalizeName(tag.Name), tag.Category))
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PostTagReconciliationTarget(group.Key, PickPreferredCategory(group.Select(tag => tag.Category))))
            .ToList();
        var desiredNames = desired
            .Select(tag => tag.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var link in post.PostTags.Where(link => link.Source == source && !desiredNames.Contains(link.Tag.Name)).ToList())
        {
            db.PostTags.Remove(link);
            post.PostTags.Remove(link);
            removed++;
        }

        var requiredNames = desired.Select(tag => tag.Name).ToList();
        var tagsByName = db.Tags.Local
            .Where(tag => requiredNames.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);

        var persistedTags = await db.Tags
            .Where(tag => requiredNames.Contains(tag.Name))
            .ToDictionaryAsync(tag => tag.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var existingTag in persistedTags)
        {
            tagsByName[existingTag.Key] = existingTag.Value;
        }

        var added = 0;
        var updatedCategories = 0;
        foreach (var desiredTag in desired)
        {
            if (!tagsByName.TryGetValue(desiredTag.Name, out var tag))
            {
                tag = new Tag
                {
                    Name = desiredTag.Name,
                    Category = desiredTag.Category
                };
                db.Tags.Add(tag);
                tagsByName[tag.Name] = tag;
            }
            else if (ShouldUpgradeCategory(tag.Category, desiredTag.Category))
            {
                tag.Category = desiredTag.Category;
                updatedCategories++;
            }

            var alreadyLinked = post.PostTags.Any(link =>
                link.Source == source
                && string.Equals(link.Tag.Name, desiredTag.Name, StringComparison.OrdinalIgnoreCase));
            if (alreadyLinked)
            {
                continue;
            }

            var link = new PostTag
            {
                PostId = post.Id,
                Tag = tag,
                Source = source
            };
            db.PostTags.Add(link);
            post.PostTags.Add(link);
            added++;
        }

        return new PostTagReconciliationResult(added, removed, updatedCategories);
    }

    private static bool ShouldUpgradeCategory(TagCategoryKind current, TagCategoryKind discovered)
        => current == TagCategoryKind.General && discovered != TagCategoryKind.General;

    private static TagCategoryKind PickPreferredCategory(IEnumerable<TagCategoryKind> categories)
    {
        foreach (var category in categories)
        {
            if (category != TagCategoryKind.General)
            {
                return category;
            }
        }

        return TagCategoryKind.General;
    }
}
