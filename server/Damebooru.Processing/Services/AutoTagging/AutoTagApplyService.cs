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
            result.Add(await PostTagReconciler.ReconcileAsync(
                _db,
                post,
                tagSource,
                scan.Tags
                    .Where(tag => tag.Provider == provider)
                    .Select(tag => new PostTagReconciliationTarget(tag.Name, tag.Category)),
                cancellationToken));
        }
    }

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

        public void Add(PostTagReconciliationResult result)
        {
            AddedTags += result.AddedTags;
            RemovedTags += result.RemovedTags;
            UpdatedTagCategories += result.UpdatedTagCategories;
        }
    }
}
