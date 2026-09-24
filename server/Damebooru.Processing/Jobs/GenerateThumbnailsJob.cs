using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Jobs;

public class GenerateThumbnailsJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.GenerateThumbnails;
    public const string JobName = "Generate Thumbnails";
    private const int BatchSize = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaEnrichmentService _mediaEnrichmentService;
    private readonly ILogger<GenerateThumbnailsJob> _logger;
    private readonly int _parallelism;

    public GenerateThumbnailsJob(
        IServiceScopeFactory scopeFactory,
        MediaEnrichmentService mediaEnrichmentService,
        ILogger<GenerateThumbnailsJob> logger,
        IOptions<DamebooruConfig> options)
    {
        _scopeFactory = scopeFactory;
        _mediaEnrichmentService = mediaEnrichmentService;
        _logger = logger;
        _parallelism = Math.Max(1, options.Value.Processing.ThumbnailParallelism);
    }

    public int DisplayOrder => 25;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Generates missing (or all) thumbnails and previews for posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var query = db.Posts.AsNoTracking();

        var totalPosts = await query.CountAsync(context.CancellationToken);
        var totalCandidates = context.Mode == JobMode.All
            ? totalPosts
            : await CountMissingGeneratedImageCandidatesAsync(db, totalPosts, context);
        _logger.LogInformation(
            "Generating thumbnails and previews for up to {Count} posts (mode: {Mode})",
            totalCandidates,
            context.Mode);

        if (totalCandidates == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "All thumbnails and previews are up to date."
            });
            return;
        }

        int processed = 0;
        int failed = 0;
        int skipped = 0;
        int lastId = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _parallelism,
            CancellationToken = context.CancellationToken
        };

        JobState BuildLiveState() => new()
        {
            ActivityText = $"Generating thumbnails and previews... ({Math.Min(totalCandidates, processed + failed)}/{totalCandidates})",
            ProgressCurrent = Math.Min(totalCandidates, processed + failed),
            ProgressTotal = totalCandidates
        };

        while (true)
        {
            var batchIds = await db.Posts
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(context.CancellationToken);
            if (batchIds.Count == 0)
            {
                break;
            }

            lastId = batchIds[^1];
            var batch = await PostEnrichmentTargets.LoadAsync(db.Posts.Where(p => batchIds.Contains(p.Id)), context.CancellationToken);
            List<PostEnrichmentTarget> toProcess;
            if (context.Mode == JobMode.All)
            {
                toProcess = batch;
            }
            else
            {
                toProcess = batch
                    .Where(target => !_mediaEnrichmentService.HasGeneratedImages(target))
                    .ToList();

                skipped += batch.Count - toProcess.Count;
            }

            await Parallel.ForEachAsync(toProcess, parallelOptions, async (target, ct) =>
            {
                try
                {
                    await _mediaEnrichmentService.GenerateGeneratedImagesAsync(target, ct);
                    Interlocked.Increment(ref processed);
                    context.Reporter.Update(BuildLiveState());
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "Failed to generate thumbnail/preview for post {Id}: {Path}", target.PostId, target.FullPath);
                    context.Reporter.Update(BuildLiveState());
                }
            });

            context.Reporter.Update(BuildLiveState());
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = Math.Min(totalCandidates, processed + failed),
            ProgressTotal = totalCandidates,
            FinalText = $"Generated {processed} thumbnail/preview sets ({failed} failed, {skipped} skipped)."
        });
        _logger.LogInformation(
            "Thumbnail/preview generation complete: {Processed} generated, {Failed} failed, {Skipped} skipped",
            processed,
            failed,
            skipped);
    }

    private async Task<int> CountMissingGeneratedImageCandidatesAsync(DamebooruDbContext db, int totalPosts, JobContext context)
    {
        context.Reporter.Update(new JobState
        {
            ActivityText = $"Scanning posts for missing thumbnails/previews... (0/{totalPosts})",
            ProgressCurrent = 0,
            ProgressTotal = totalPosts,
        });

        var lastId = 0;
        var scanned = 0;
        var missingCount = 0;

        while (true)
        {
            var batchIds = await db.Posts
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(context.CancellationToken);
            if (batchIds.Count == 0)
            {
                break;
            }

            lastId = batchIds[^1];
            var batch = await PostEnrichmentTargets.LoadAsync(db.Posts.Where(p => batchIds.Contains(p.Id)), context.CancellationToken);
            scanned += batchIds.Count;
            missingCount += batch.Count(target => !_mediaEnrichmentService.HasGeneratedImages(target));

            context.Reporter.Update(new JobState
            {
                ActivityText = $"Scanning posts for missing thumbnails/previews... ({scanned}/{totalPosts})",
                ProgressCurrent = scanned,
                ProgressTotal = totalPosts,
            });
        }

        return missingCount;
    }
}
