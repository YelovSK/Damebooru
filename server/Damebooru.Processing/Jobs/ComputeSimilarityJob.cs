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

public class ComputeSimilarityJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ComputeSimilarity;
    public const string JobName = "Compute Similarity";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaEnrichmentService _mediaEnrichmentService;
    private readonly ILogger<ComputeSimilarityJob> _logger;
    private readonly int _parallelism;

    public ComputeSimilarityJob(
        IServiceScopeFactory scopeFactory,
        MediaEnrichmentService mediaEnrichmentService,
        ILogger<ComputeSimilarityJob> logger,
        IOptions<DamebooruConfig> config)
    {
        _scopeFactory = scopeFactory;
        _mediaEnrichmentService = mediaEnrichmentService;
        _logger = logger;
        _parallelism = Math.Max(1, config.Value.Processing.SimilarityParallelism);
    }

    public int DisplayOrder => 30;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Computes 256-bit PDQ hashes for image posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var query = db.Posts.AsNoTracking().Where(p => p.ContentType.StartsWith("image/"));
        if (context.Mode == JobMode.Missing)
        {
            query = query.Where(p => string.IsNullOrEmpty(p.PdqHash256));
        }

        var totalCandidates = await query.CountAsync(context.CancellationToken);
        _logger.LogInformation("Computing similarity hashes for {Count} candidate posts (mode: {Mode})", totalCandidates, context.Mode);

        if (totalCandidates == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "All similarity hashes are up to date."
            });
            return;
        }

        var completed = 0;
        int processed = 0;
        int failed = 0;
        var lastId = 0;

        JobState BuildLiveState(string phase) => new()
        {
            ActivityText = $"{phase} ({Math.Min(totalCandidates, completed)}/{totalCandidates})",
            ProgressCurrent = Math.Min(totalCandidates, completed),
            ProgressTotal = totalCandidates
        };

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
            var results = new ConcurrentBag<PostSimilarityResult>();

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
                        var result = await _mediaEnrichmentService.ComputeSimilarityAsync(target, ct);
                        if (result != null)
                        {
                            results.Add(result);
                            Interlocked.Increment(ref processed);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to compute similarity hash for post {Id}: {Path}", target.PostId, target.FullPath);
                    }
                    finally
                    {
                        Interlocked.Increment(ref completed);
                        context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
                    }
                });

            var entities = await db.Posts
                .Where(p => batchIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, context.CancellationToken);

            foreach (var result in results)
            {
                if (entities.TryGetValue(result.PostId, out var entity))
                {
                    entity.PdqHash256 = result.PdqHash256;
                }
            }

            await db.SaveChangesAsync(context.CancellationToken);

            context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = Math.Min(totalCandidates, completed),
            ProgressTotal = totalCandidates,
            FinalText = $"Computed {processed} similarity hashes ({failed} failed)."
        });
        _logger.LogInformation("Similarity hash computation complete: {Processed} processed, {Failed} failed", processed, failed);
    }
}
