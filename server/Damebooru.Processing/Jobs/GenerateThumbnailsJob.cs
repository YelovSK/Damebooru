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
    public string Description => "Generates missing (or all) thumbnails for posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var query = db.PostFiles
            .AsNoTracking()
            .Where(pf => !string.IsNullOrEmpty(pf.ContentHash))
            .AsQueryable();

        var totalFiles = await query.CountAsync(context.CancellationToken);
        var totalCandidates = context.Mode == JobMode.All
            ? totalFiles
            : await CountMissingThumbnailCandidatesAsync(query, totalFiles, context);
        _logger.LogInformation(
            "Generating thumbnails for up to {Count} files (mode: {Mode})",
            totalCandidates,
            context.Mode);

        if (totalCandidates == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "All thumbnails are up to date."
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
            ActivityText = $"Generating thumbnails... ({Math.Min(totalCandidates, processed + failed)}/{totalCandidates})",
            ProgressCurrent = Math.Min(totalCandidates, processed + failed),
            ProgressTotal = totalCandidates
        };

        while (true)
        {
            var batch = await query
                .Where(pf => pf.Id > lastId)
                .OrderBy(pf => pf.Id)
                .Select(pf => new PostFileEnrichmentTarget(
                    pf.Id,
                    pf.LibraryId,
                    pf.ContentHash,
                    pf.RelativePath,
                    pf.Library.Path))
                .Take(BatchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].PostFileId;
            List<PostFileEnrichmentTarget> toProcess;
            if (context.Mode == JobMode.All)
            {
                toProcess = batch;
            }
            else
            {
                toProcess = batch
                    .Where(target => !_mediaEnrichmentService.HasThumbnail(target))
                    .ToList();

                skipped += batch.Count - toProcess.Count;
            }

            await Parallel.ForEachAsync(toProcess, parallelOptions, async (postFile, ct) =>
            {
                try
                {
                    await _mediaEnrichmentService.GenerateThumbnailAsync(postFile, ct);
                    Interlocked.Increment(ref processed);
                    context.Reporter.Update(BuildLiveState());
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "Failed to generate thumbnail for post file {Id}: {Path}", postFile.PostFileId, postFile.RelativePath);
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
            FinalText = $"Generated {processed} thumbnails ({failed} failed, {skipped} skipped)."
        });
        _logger.LogInformation(
            "Thumbnail generation complete: {Processed} generated, {Failed} failed, {Skipped} skipped",
            processed,
            failed,
            skipped);
    }

    private async Task<int> CountMissingThumbnailCandidatesAsync(IQueryable<PostFile> query, int totalFiles, JobContext context)
    {
        context.Reporter.Update(new JobState
        {
            ActivityText = $"Scanning files for missing thumbnails... (0/{totalFiles})",
            ProgressCurrent = 0,
            ProgressTotal = totalFiles,
        });

        var lastId = 0;
        var scanned = 0;
        var missingCount = 0;

        while (true)
        {
            var batch = await query
                .Where(pf => pf.Id > lastId)
                .OrderBy(pf => pf.Id)
                .Select(pf => new PostFileEnrichmentTarget(
                    pf.Id,
                    pf.LibraryId,
                    pf.ContentHash,
                    pf.RelativePath,
                    pf.Library.Path))
                .Take(BatchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].PostFileId;
            scanned += batch.Count;
            missingCount += batch.Count(target => !_mediaEnrichmentService.HasThumbnail(target));

            context.Reporter.Update(new JobState
            {
                ActivityText = $"Scanning files for missing thumbnails... ({scanned}/{totalFiles})",
                ProgressCurrent = scanned,
                ProgressTotal = totalFiles,
            });
        }

        return missingCount;
    }
}
