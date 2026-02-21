using Bakabooru.Core;
using Bakabooru.Core.Config;
using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Bakabooru.Processing.Jobs;

public class ComputeSimilarityJob : IJob
{
    public const string JobKey = "compute-similarity";
    public const string JobName = "Compute Similarity";

    private sealed record SimilarityCandidate(int Id, string RelativePath, string LibraryPath);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ComputeSimilarityJob> _logger;
    private readonly int _parallelism;

    public ComputeSimilarityJob(
        IServiceScopeFactory scopeFactory,
        ILogger<ComputeSimilarityJob> logger,
        IOptions<BakabooruConfig> config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _parallelism = Math.Max(1, config.Value.Processing.SimilarityParallelism);
    }

    public int DisplayOrder => 30;
    public string Key => JobKey;
    public string Name => JobName;
    public string Description => "Computes perceptual hashes (dHash + pHash) for image posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
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
            context.State.Report(new JobState
            {
                Phase = "Completed",
                Processed = 0,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Summary = "All similarity hashes are up to date."
            });
            return;
        }

        var scanned = 0;
        int processed = 0;
        int failed = 0;
        var lastId = 0;

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
                context.State.Report(new JobState
                {
                    Phase = "Scanning candidates...",
                    Processed = scanned,
                    Total = totalCandidates,
                    Succeeded = processed,
                    Failed = failed,
                    Summary = $"Scanned {scanned}/{totalCandidates} candidates"
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
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to compute similarity hash for post {Id}: {Path}", post.Id, post.RelativePath);
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

            context.State.Report(new JobState
            {
                Phase = "Computing similarity hashes...",
                Processed = scanned,
                Total = totalCandidates,
                Succeeded = processed,
                Failed = failed,
                Summary = $"Computed {processed} hashes ({failed} failed)"
            });
        }

        context.State.Report(new JobState
        {
            Phase = "Completed",
            Processed = scanned,
            Total = totalCandidates,
            Succeeded = processed,
            Failed = failed,
            Summary = $"Computed {processed} similarity hashes ({failed} failed)."
        });
        _logger.LogInformation("Similarity hash computation complete: {Processed} processed, {Failed} failed", processed, failed);
    }
}
