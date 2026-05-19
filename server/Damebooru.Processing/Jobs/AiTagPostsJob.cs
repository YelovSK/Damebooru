using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services.AiTagging;
using Damebooru.Processing.Services.AutoTagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public sealed class AiTagPostsJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.AiTagPosts;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiTagPostsJob> _logger;

    public AiTagPostsJob(IServiceScopeFactory scopeFactory, ILogger<AiTagPostsJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 81;
    public JobKey Key => JobKey;
    public string Name => "AI Auto-Tag Posts";
    public string Description => "Runs the local AI tagging service for image posts and applies AI-owned tags.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<DamebooruConfig>();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        if (!config.AiTagging.Enabled)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "AI tagging is not enabled."
            });
            return;
        }

        var candidatePostIds = await GetCandidatePostIdsAsync(db, context.Mode, context.CancellationToken);
        var total = candidatePostIds.Count;
        if (total == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "No posts require AI tagging."
            });
            return;
        }

        var processed = 0;
        var completed = 0;
        var failed = 0;
        var tagsAdded = 0;
        var tagsRemoved = 0;
        var categoriesUpdated = 0;

        foreach (var postId in candidatePostIds)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            using var postScope = _scopeFactory.CreateScope();
            var aiTaggingService = postScope.ServiceProvider.GetRequiredService<AiTaggingService>();

            try
            {
                var result = await aiTaggingService.ApplyAsync(postId, context.CancellationToken);
                if (result.IsSuccess && result.Value != null)
                {
                    completed++;
                    tagsAdded += result.Value.AddedTags;
                    tagsRemoved += result.Value.RemovedTags;
                    categoriesUpdated += result.Value.UpdatedTagCategories;
                }
                else
                {
                    failed++;
                    _logger.LogWarning(
                        "AI tagging failed for post {PostId}: {Message}",
                        postId,
                        result.Message ?? "Unknown error.");
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "AI tagging failed for post {PostId}", postId);
            }

            processed++;
            context.Reporter.Update(new JobState
            {
                ActivityText = $"AI tagging posts... ({processed}/{total})",
                ProgressCurrent = processed,
                ProgressTotal = total
            });
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = processed,
            ProgressTotal = total,
            FinalText = $"Completed {completed}, failed {failed}, added {tagsAdded} tags, removed {tagsRemoved} tags, updated {categoriesUpdated} categories."
        });
    }

    private static async Task<List<int>> GetCandidatePostIdsAsync(
        DamebooruDbContext db,
        JobMode mode,
        CancellationToken cancellationToken)
    {
        var imagePosts = db.Posts
            .AsNoTracking()
            .Where(p => p.PostFiles.Any(pf => EF.Functions.Like(pf.ContentType, "image/%")));

        var candidatePostIds = mode == JobMode.All
            ? await imagePosts
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken)
            : await imagePosts
                .Where(p => !p.PostTags.Any(pt => pt.Source == PostTagSource.Ai))
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

        return await AutoTagCandidateFilter.ExcludeAutoTagIgnoredPathsAsync(db, candidatePostIds, cancellationToken);
    }
}
