using Damebooru.Core.Entities;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class AutoTagApplyService
{
    private readonly DamebooruDbContext _db;

    public AutoTagApplyService(DamebooruDbContext db)
    {
        _db = db;
    }

    public async Task<AutoTagApplyResult> ApplyScanAsync(int postId, CancellationToken cancellationToken = default)
    {
        var scan = await _db.PostAutoTagScans
            .Include(s => s.Steps)
            .Include(s => s.Sources)
            .Include(s => s.Tags)
            .FirstOrDefaultAsync(s => s.PostId == postId, cancellationToken)
            ?? throw new InvalidOperationException($"No auto-tag scan exists for post {postId}.");

        var post = await _db.Posts
            .Include(p => p.Sources)
            .Include(p => p.PostTags)
                .ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == postId, cancellationToken)
            ?? throw new InvalidOperationException($"Post {postId} was not found.");

        var result = new MutableApplyResult();

        ApplySources(scan, post, result);
        await ApplyTagsAsync(scan, post, result, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return new AutoTagApplyResult(result.AddedTags, result.RemovedTags, result.UpdatedTagCategories, result.AddedSources);
    }

    private static void ApplySources(PostAutoTagScan scan, Post post, MutableApplyResult result)
    {
        var orderedSources = post.Sources.OrderBy(source => source.Order).ToList();
        var existingUrls = orderedSources.Select(source => source.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var url in scan.Sources.Select(source => source.Url).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existingUrls.Add(url))
            {
                post.Sources.Add(new PostSource
                {
                    Url = url,
                    Order = orderedSources.Count + result.AddedSources
                });
                result.AddedSources++;
            }
        }
    }

    private async Task ApplyTagsAsync(PostAutoTagScan scan, Post post, MutableApplyResult result, CancellationToken cancellationToken)
    {
        foreach (var provider in new[] { AutoTagProvider.Danbooru, AutoTagProvider.Gelbooru })
        {
            var step = scan.Steps.FirstOrDefault(s => s.Provider == provider);
            if (step == null || step.Status is AutoTagScanStepStatus.RetryableFailure or AutoTagScanStepStatus.PermanentFailure or AutoTagScanStepStatus.Pending or AutoTagScanStepStatus.Running)
            {
                continue;
            }

            var tagSource = ToPostTagSource(provider);
            var desiredScanTags = scan.Tags.Where(tag => tag.Provider == provider).ToList();
            var desiredNames = desiredScanTags.Select(tag => tag.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingLinks = post.PostTags.Where(link => link.Source == tagSource).ToList();
            foreach (var link in existingLinks.Where(link => !desiredNames.Contains(link.Tag.Name)).ToList())
            {
                _db.PostTags.Remove(link);
                post.PostTags.Remove(link);
                result.RemovedTags++;
            }

            var requiredNames = desiredScanTags.Select(tag => tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tagsByName = _db.Tags.Local
                .Where(tag => requiredNames.Contains(tag.Name))
                .ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);

            var persistedTags = await _db.Tags
                .Where(tag => requiredNames.Contains(tag.Name))
                .ToDictionaryAsync(tag => tag.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var existingTag in persistedTags)
            {
                tagsByName[existingTag.Key] = existingTag.Value;
            }

            foreach (var desiredTag in desiredScanTags)
            {
                if (!tagsByName.TryGetValue(desiredTag.Name, out var tag))
                {
                    tag = new Tag
                    {
                        Name = desiredTag.Name,
                        Category = desiredTag.Category
                    };
                    _db.Tags.Add(tag);
                    tagsByName[tag.Name] = tag;
                }
                else if (ShouldUpgradeCategory(tag.Category, desiredTag.Category))
                {
                    tag.Category = desiredTag.Category;
                    result.UpdatedTagCategories++;
                }

                var alreadyLinked = post.PostTags.Any(link => link.Source == tagSource && string.Equals(link.Tag.Name, desiredTag.Name, StringComparison.OrdinalIgnoreCase));
                if (!alreadyLinked)
                {
                    var link = new PostTag
                    {
                        PostId = post.Id,
                        Tag = tag,
                        Source = tagSource
                    };
                    _db.PostTags.Add(link);
                    post.PostTags.Add(link);
                    result.AddedTags++;
                }
            }
        }
    }

    private static bool ShouldUpgradeCategory(TagCategoryKind current, TagCategoryKind discovered)
        => current == TagCategoryKind.General && discovered != TagCategoryKind.General;

    private static PostTagSource ToPostTagSource(AutoTagProvider provider)
        => provider switch
        {
            AutoTagProvider.Danbooru => PostTagSource.Danbooru,
            AutoTagProvider.Gelbooru => PostTagSource.Gelbooru,
            _ => throw new InvalidOperationException($"Provider {provider} does not map to a post tag source.")
        };

    private sealed class MutableApplyResult
    {
        public int AddedTags { get; set; }
        public int RemovedTags { get; set; }
        public int UpdatedTagCategories { get; set; }
        public int AddedSources { get; set; }
    }
}
