using Damebooru.Core;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services.AutoTagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public sealed class AutoTagPostsJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.AutoTagPosts;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoTagPostsJob> _logger;

    public AutoTagPostsJob(IServiceScopeFactory scopeFactory, ILogger<AutoTagPostsJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 80;
    public JobKey Key => JobKey;
    public string Name => "Auto-Tag Posts";
    public string Description => "Runs external auto-tag scans for posts and applies provider-owned tags and source URLs.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var configValidator = scope.ServiceProvider.GetRequiredService<AutoTagConfigurationValidator>();

        try
        {
            configValidator.EnsureConfigured();
        }
        catch (InvalidOperationException ex)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Failed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = ex.Message
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
                FinalText = "No posts require auto-tagging."
            });
            return;
        }

        var processed = 0;
        var completed = 0;
        var partial = 0;
        var failed = 0;
        var tagsAdded = 0;
        var tagsRemoved = 0;
        var sourcesAdded = 0;
        string? earlyStopReason = null;

        foreach (var postId in candidatePostIds)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            using var postScope = _scopeFactory.CreateScope();
            var scanService = postScope.ServiceProvider.GetRequiredService<AutoTagScanService>();
            var applyService = postScope.ServiceProvider.GetRequiredService<AutoTagApplyService>();

            try
            {
                var scanResult = await scanService.ScanPostAsync(postId, context.CancellationToken);
                if (scanResult.ShouldApply)
                {
                    var applyResult = await applyService.ApplyScanAsync(postId, context.CancellationToken);
                    tagsAdded += applyResult.AddedTags;
                    tagsRemoved += applyResult.RemovedTags;
                    sourcesAdded += applyResult.AddedSources;
                }

                if (scanResult.Status == AutoTagScanStatus.Completed)
                {
                    completed++;
                }
                else if (scanResult.Status == AutoTagScanStatus.Partial)
                {
                    partial++;
                }
                else
                {
                    failed++;
                }

                if (scanResult.Directive != null)
                {
                    if (scanResult.Directive.Delay is { } delay && delay > TimeSpan.Zero && !scanResult.Directive.StopCurrentRun)
                    {
                        context.Reporter.Update(new JobState
                        {
                            ActivityText = $"Waiting {delay.TotalSeconds:0}s for {scanResult.Directive.Provider} rate limit...",
                            ProgressCurrent = processed,
                            ProgressTotal = total
                        });
                        await Task.Delay(delay, context.CancellationToken);
                    }

                    if (scanResult.Directive.StopCurrentRun)
                    {
                        earlyStopReason = scanResult.Directive.Reason;
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Auto-tagging failed for post {PostId}", postId);
            }

            processed++;
            context.Reporter.Update(new JobState
            {
                ActivityText = $"Auto-tagging posts... ({processed}/{total})",
                ProgressCurrent = processed,
                ProgressTotal = total
            });

            if (earlyStopReason != null)
            {
                break;
            }
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = processed,
            ProgressTotal = total,
            FinalText = earlyStopReason == null
                ? $"Completed {completed}, partial {partial}, failed {failed}, added {tagsAdded} tags, removed {tagsRemoved} tags, added {sourcesAdded} sources."
                : $"Stopped early due to provider limit: {earlyStopReason}. Processed {processed}/{total}, completed {completed}, partial {partial}, failed {failed}, added {tagsAdded} tags, removed {tagsRemoved} tags, added {sourcesAdded} sources."
        });
    }

    private static async Task<List<int>> GetCandidatePostIdsAsync(DamebooruDbContext db, JobMode mode, CancellationToken cancellationToken)
    {
        var imagePosts = db.Posts
            .AsNoTracking()
            .Where(p => EF.Functions.Like(p.ContentType, "image/%"));

        if (mode == JobMode.All)
        {
            return await imagePosts
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        return await imagePosts
            .GroupJoin(
                db.PostAutoTagScans.AsNoTracking(),
                post => post.Id,
                scan => scan.PostId,
                (post, scans) => new { Post = post, Scan = scans.FirstOrDefault() })
            .Where(x => x.Scan == null
                || x.Scan.ContentHash != x.Post.ContentHash
                || x.Scan.Status == AutoTagScanStatus.Pending
                || x.Scan.Status == AutoTagScanStatus.InProgress
                || x.Scan.Status == AutoTagScanStatus.Partial
                || (x.Scan.Status == AutoTagScanStatus.Completed
                    && !db.PostAutoTagScanCandidates.Any(candidate => candidate.ScanId == x.Scan.Id)
                    && x.Scan.DiscoveryVersion != AutoTagDiscoveryPlan.Version)
                || db.PostAutoTagScanSteps.Any(step => step.ScanId == x.Scan!.Id && step.Status == AutoTagScanStepStatus.RetryableFailure && step.NextRetryAtUtc <= now))
            .OrderBy(x => x.Post.Id)
            .Select(x => x.Post.Id)
            .ToListAsync(cancellationToken);
    }
}
