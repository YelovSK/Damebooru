using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Damebooru.Processing.Jobs;

public class ComputeSimilarityJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ComputeSimilarity;
    public const string JobName = "Compute Similarity";

    private sealed record SimilarityCandidate(int Id, string RelativePath, string LibraryPath);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ComputeSimilarityJob> _logger;
    private readonly int _parallelism;

    public ComputeSimilarityJob(
        IServiceScopeFactory scopeFactory,
        ILogger<ComputeSimilarityJob> logger,
        IOptions<DamebooruConfig> config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _parallelism = Math.Max(1, config.Value.Processing.SimilarityParallelism);
    }

    public int DisplayOrder => 30;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Computes perceptual hashes (dHash + pHash) for image posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var similarityService = scope.ServiceProvider.GetRequiredService<ISimilarityService>();

        // Only images have perceptual hashes
        var query = db.Posts.AsNoTracking().AsQueryable();
        if (context.Mode == JobMode.Missing)
        {
            query = query.Where(p =>
                p.PerceptualHash == null || p.PerceptualHash == 0 ||
                p.PerceptualHashP == null || p.PerceptualHashP == 0);
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

        var scanned = 0;
        int processed = 0;
        int failed = 0;
        var lastId = 0;

        JobState BuildLiveState(string phase) => new()
        {
            ActivityText = phase,
            ProgressCurrent = Math.Min(totalCandidates, processed + failed),
            ProgressTotal = totalCandidates
        };

        const int batchSize = 100;
        while (true)
        {
            var batch = await query
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => new SimilarityCandidate(p.Id, p.RelativePath, p.Library.Path))
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].Id;
            scanned += batch.Count;

            // Similarity hashing only applies to image files.
            var imageBatch = batch
                .Where(p => SupportedMedia.IsImage(Path.GetExtension(p.RelativePath)))
                .ToList();

            if (imageBatch.Count == 0)
            {
                context.Reporter.Update(new JobState
                {
                    ActivityText = "Scanning candidates...",
                    ProgressCurrent = scanned,
                    ProgressTotal = totalCandidates
                });
                continue;
            }

            var results = new ConcurrentBag<(int PostId, SimilarityHashes? Hashes)>();

            await Parallel.ForEachAsync(
                imageBatch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _parallelism,
                    CancellationToken = context.CancellationToken
                },
                async (post, ct) =>
                {
                    try
                    {
                        var fullPath = Path.Combine(post.LibraryPath, post.RelativePath);
                        var hashes = await similarityService.ComputeHashesAsync(fullPath, ct);

                        results.Add((post.Id, hashes));
                        Interlocked.Increment(ref processed);
                        context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to compute similarity hash for post {Id}: {Path}", post.Id, post.RelativePath);
                        context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
                    }
                });

            var entityIds = imageBatch.Select(p => p.Id).ToList();
            var entities = await db.Posts
                .Where(p => entityIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, context.CancellationToken);

            foreach (var result in results)
            {
                if (entities.TryGetValue(result.PostId, out var entity))
                {
                    entity.PerceptualHash = result.Hashes?.DHash;
                    entity.PerceptualHashP = result.Hashes?.PHash;
                }
            }

            await db.SaveChangesAsync(context.CancellationToken);

            context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = scanned,
            ProgressTotal = totalCandidates,
            FinalText = $"Computed {processed} similarity hashes ({failed} failed).",
            ResultSchemaVersion = 1,
            ResultJson = JsonSerializer.Serialize(new
            {
                scanned,
                totalCandidates,
                processed,
                failed,
            })
        });
        _logger.LogInformation("Similarity hash computation complete: {Processed} processed, {Failed} failed", processed, failed);
    }
}
