using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public class ApplyFolderTagsJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ApplyFolderTags;
    public const string JobName = "Apply Folder Tags";

    private sealed record FolderTagCandidate(int Id, string RelativePath);
    private sealed record ExistingFolderTagLink(int PostId, int TagId, string TagName);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApplyFolderTagsJob> _logger;

    public ApplyFolderTagsJob(IServiceScopeFactory scopeFactory, ILogger<ApplyFolderTagsJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 70;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Adds tags to posts based on parent folders (spaces become underscores).";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var folderTagging = scope.ServiceProvider.GetRequiredService<FolderTaggingService>();

        var totalPosts = await db.Posts.AsNoTracking().CountAsync(context.CancellationToken);
        if (totalPosts == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "No posts to process."
            });
            return;
        }

        const int batchSize = 500;
        var lastId = 0;
        var processed = 0;
        var updatedPosts = 0;
        var addedTags = 0;
        var removedTags = 0;
        var skipped = 0;
        var failed = 0;

        while (true)
        {
            var batch = await db.Posts
                .AsNoTracking()
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => new FolderTagCandidate(p.Id, p.RelativePath))
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].Id;
            processed += batch.Count;

            try
            {
                var postIds = batch.Select(p => p.Id).ToList();

                var existingFolderLinks = await db.PostTags
                    .AsNoTracking()
                    .Where(pt => postIds.Contains(pt.PostId) && pt.Source == PostTagSource.Folder)
                    .Select(pt => new ExistingFolderTagLink(pt.PostId, pt.TagId, pt.Tag.Name))
                    .ToListAsync(context.CancellationToken);

                var existingFolderByPost = existingFolderLinks
                    .GroupBy(x => x.PostId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.ToList());

                var postsToRemoveLinks = new List<PostTag>();
                var plans = new List<(int PostId, List<string> TagsToAdd)>(batch.Count);
                var neededTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var post in batch)
                {
                    var desiredFolderTags = folderTagging.BuildPlan(post.RelativePath).FolderTags
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var existingFolderLinksForPost = existingFolderByPost.TryGetValue(post.Id, out var links)
                        ? links
                        : [];
                    var existingFolderNames = existingFolderLinksForPost
                        .Select(link => link.TagName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var tagsToAdd = desiredFolderTags
                        .Where(name => !existingFolderNames.Contains(name))
                        .ToList();
                    var tagIdsToRemove = existingFolderLinksForPost
                        .Where(link => !desiredFolderTags.Contains(link.TagName))
                        .Select(link => link.TagId)
                        .ToHashSet();

                    if (tagIdsToRemove.Count > 0)
                    {
                        postsToRemoveLinks.AddRange(tagIdsToRemove.Select(tagId => new PostTag
                        {
                            PostId = post.Id,
                            TagId = tagId,
                            Source = PostTagSource.Folder
                        }));
                    }

                    if (tagsToAdd.Count > 0)
                    {
                        plans.Add((post.Id, tagsToAdd));
                    }

                    foreach (var tagName in tagsToAdd)
                    {
                        neededTagNames.Add(tagName);
                    }

                    if (tagsToAdd.Count == 0 && tagIdsToRemove.Count == 0)
                    {
                        skipped++;
                    }
                }

                if (postsToRemoveLinks.Count > 0)
                {
                    db.PostTags.RemoveRange(postsToRemoveLinks);
                    removedTags += postsToRemoveLinks.Count;
                }

                if (plans.Count > 0 || postsToRemoveLinks.Count > 0)
                {
                    var tagsByName = await db.Tags
                        .Where(t => neededTagNames.Contains(t.Name))
                        .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, context.CancellationToken);

                    foreach (var tagName in neededTagNames)
                    {
                        if (tagsByName.ContainsKey(tagName))
                        {
                            continue;
                        }

                        var tag = new Tag { Name = tagName };
                        db.Tags.Add(tag);
                        tagsByName[tagName] = tag;
                    }

                    foreach (var (postId, tagsToAdd) in plans)
                    {
                        var postAdded = 0;
                        foreach (var tagName in tagsToAdd)
                        {
                            db.PostTags.Add(new PostTag
                            {
                                PostId = postId,
                                Tag = tagsByName[tagName],
                                Source = PostTagSource.Folder
                            });
                            postAdded++;
                        }

                        if (postAdded > 0 || postsToRemoveLinks.Any(link => link.PostId == postId))
                        {
                            updatedPosts++;
                            addedTags += postAdded;
                        }
                    }

                    if (postsToRemoveLinks.Count > 0)
                    {
                        updatedPosts += postsToRemoveLinks
                            .Select(link => link.PostId)
                            .Where(postId => plans.All(plan => plan.PostId != postId))
                            .Distinct()
                            .Count();
                    }

                    await db.SaveChangesAsync(context.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                failed += batch.Count;
                _logger.LogWarning(ex, "Failed processing folder tags for batch ending at post id {LastId}", lastId);
            }

            context.Reporter.Update(new JobState
            {
                ActivityText = $"Applying folder tags... ({processed}/{totalPosts})",
                ProgressCurrent = processed,
                ProgressTotal = totalPosts
            });
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = processed,
            ProgressTotal = totalPosts,
            FinalText = $"Updated {updatedPosts} posts, added {addedTags} folder tags, removed {removedTags} stale folder tags, skipped {skipped}, failed {failed}."
        });
    }
}
