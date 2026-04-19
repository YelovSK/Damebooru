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
                .Select(p => new FolderTagCandidate(
                    p.Id,
                    p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.RelativePath).FirstOrDefault() ?? string.Empty))
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
                var folderTagLinksBefore = await db.PostTags
                    .AsNoTracking()
                    .CountAsync(pt => postIds.Contains(pt.PostId) && pt.Source == PostTagSource.Folder, context.CancellationToken);

                await folderTagging.SyncPostFolderTagsAsync(db, postIds, context.CancellationToken);
                await db.SaveChangesAsync(context.CancellationToken);

                var folderTagLinksAfter = await db.PostTags
                    .AsNoTracking()
                    .CountAsync(pt => postIds.Contains(pt.PostId) && pt.Source == PostTagSource.Folder, context.CancellationToken);

                if (folderTagLinksAfter != folderTagLinksBefore)
                {
                    updatedPosts += batch.Count;
                    if (folderTagLinksAfter > folderTagLinksBefore)
                    {
                        addedTags += folderTagLinksAfter - folderTagLinksBefore;
                    }
                    else
                    {
                        removedTags += folderTagLinksBefore - folderTagLinksAfter;
                    }
                }
                else
                {
                    skipped += batch.Count;
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
