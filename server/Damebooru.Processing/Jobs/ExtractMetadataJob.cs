using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Damebooru.Processing.Jobs;

public class ExtractMetadataJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ExtractMetadata;
    public const string JobName = "Extract Metadata";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaEnrichmentService _mediaEnrichmentService;
    private readonly ILogger<ExtractMetadataJob> _logger;
    private readonly int _parallelism;

    public ExtractMetadataJob(
        IServiceScopeFactory scopeFactory,
        MediaEnrichmentService mediaEnrichmentService,
        ILogger<ExtractMetadataJob> logger,
        IOptions<DamebooruConfig> config)
    {
        _scopeFactory = scopeFactory;
        _mediaEnrichmentService = mediaEnrichmentService;
        _logger = logger;
        _parallelism = Math.Max(1, config.Value.Processing.MetadataParallelism);
    }

    public int DisplayOrder => 20;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Extracts dimensions and content type for posts.";
    public bool SupportsAllMode => true;

    public async Task<string> ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var query = db.Posts.AsNoTracking().AsQueryable();
        if (context.Mode == JobMode.Missing)
        {
            query = query.Where(p => p.Width == null);
        }

        var totalPosts = await query.CountAsync(context.CancellationToken);

        _logger.LogInformation("Extracting metadata for {Count} posts (mode: {Mode})", totalPosts, context.Mode);

        if (totalPosts == 0)
        {
            return "All metadata is up to date.";
        }

        int processed = 0;
        int failed = 0;

        void ReportProgress() => context.Reporter.Report("Extracting metadata...", processed + failed, totalPosts);

        var lastId = 0;

        const int batchSize = 100;
        while (true)
        {
            var batchIds = await query
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batchIds.Count == 0)
            {
                break;
            }

            lastId = batchIds[^1];
            var batch = await PostEnrichmentTargets.LoadAsync(db.Posts.Where(p => batchIds.Contains(p.Id)), context.CancellationToken);
            var results = new ConcurrentBag<PostMetadataResult>();

            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _parallelism,
                    CancellationToken = context.CancellationToken
                },
                async (target, ct) =>
                {
                    try
                    {
                        results.Add(await _mediaEnrichmentService.ExtractMetadataAsync(target, ct));
                        Interlocked.Increment(ref processed);
                        ReportProgress();
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to extract metadata for post {Id}: {Path}", target.PostId, target.FullPath);
                        ReportProgress();
                    }
                });

            var entities = await db.Posts
                .Where(p => batchIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, context.CancellationToken);

            foreach (var result in results)
            {
                if (entities.TryGetValue(result.PostId, out var entity))
                {
                    entity.Width = result.Width;
                    entity.Height = result.Height;
                }
            }

            await db.SaveChangesAsync(context.CancellationToken);

            ReportProgress();
        }

        _logger.LogInformation("Metadata extraction complete: {Processed} processed, {Failed} failed", processed, failed);
        return $"Extracted metadata for {processed} posts ({failed} failed).";
    }
}
