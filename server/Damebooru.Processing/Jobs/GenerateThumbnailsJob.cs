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

    public async Task<string> ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var (candidateIds, skipped) = await LoadCandidateIdsAsync(db, context);
        var totalCandidates = candidateIds.Count;
        _logger.LogInformation(
            "Generating thumbnails and previews for up to {Count} posts (mode: {Mode})",
            totalCandidates,
            context.Mode);

        if (totalCandidates == 0)
        {
            return "All thumbnails and previews are up to date.";
        }

        int processed = 0;
        int failed = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _parallelism,
            CancellationToken = context.CancellationToken
        };

        void ReportProgress() => context.Reporter.Report("Generating thumbnails and previews...", processed + failed, totalCandidates);

        foreach (var batchIds in candidateIds.Chunk(BatchSize))
        {
            var batch = await PostEnrichmentTargets.LoadAsync(db.Posts.Where(p => batchIds.Contains(p.Id)), context.CancellationToken);

            await Parallel.ForEachAsync(batch, parallelOptions, async (target, ct) =>
            {
                try
                {
                    await _mediaEnrichmentService.GenerateGeneratedImagesAsync(target, ct);
                    Interlocked.Increment(ref processed);
                    ReportProgress();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "Failed to generate thumbnail/preview for post {Id}: {Path}", target.PostId, target.FullPath);
                    ReportProgress();
                }
            });

            ReportProgress();
        }

        _logger.LogInformation(
            "Thumbnail/preview generation complete: {Processed} generated, {Failed} failed, {Skipped} skipped",
            processed,
            failed,
            skipped);
        return $"Generated {processed} thumbnail/preview sets ({failed} failed, {skipped} skipped).";
    }

    private async Task<(List<int> CandidateIds, int Skipped)> LoadCandidateIdsAsync(DamebooruDbContext db, JobContext context)
    {
        var posts = db.Posts.AsNoTracking().OrderBy(p => p.Id);
        if (context.Mode == JobMode.All)
        {
            return (await posts.Select(p => p.Id).ToListAsync(context.CancellationToken), 0);
        }

        context.Reporter.Report("Checking for missing thumbnails/previews...");

        var existingHashes = _mediaEnrichmentService.GetContentHashesWithGeneratedImages();
        var rows = await posts
            .Select(p => new { p.Id, p.ContentHash })
            .ToListAsync(context.CancellationToken);
        var candidateIds = rows
            .Where(row => !existingHashes.Contains(row.ContentHash))
            .Select(row => row.Id)
            .ToList();

        return (candidateIds, rows.Count - candidateIds.Count);
    }
}
