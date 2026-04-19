using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
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

    private sealed record SimilarityCandidate(int PostId, int PostFileId, string RelativePath, string LibraryPath);

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
    public string Description => "Computes 256-bit PDQ hashes for image posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var similarityService = scope.ServiceProvider.GetRequiredService<ISimilarityService>();

        // Only images have PDQ hashes
        var query = db.Posts.AsNoTracking().AsQueryable();
        if (context.Mode == JobMode.Missing)
        {
            query = query.Where(p => p.PostFiles.Any(pf => string.IsNullOrEmpty(pf.PdqHash256)));
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
            var batch = await query
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => new SimilarityCandidate(
                    p.Id,
                    p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.Id).FirstOrDefault(),
                    p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.RelativePath).FirstOrDefault() ?? string.Empty,
                    p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.Library.Path).FirstOrDefault() ?? string.Empty))
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].PostId;
            scanned += batch.Count;

            // Similarity hashing only applies to image files.
            var imageBatch = batch
                .Where(p => SupportedMedia.IsImage(Path.GetExtension(p.RelativePath)))
                .ToList();

            if (imageBatch.Count == 0)
            {
                Interlocked.Add(ref completed, batch.Count);
                context.Reporter.Update(new JobState
                {
                    ActivityText = $"Scanning candidates... ({Math.Min(totalCandidates, completed)}/{totalCandidates})",
                    ProgressCurrent = Math.Min(totalCandidates, completed),
                    ProgressTotal = totalCandidates
                });
                continue;
            }

            var nonImageCount = batch.Count - imageBatch.Count;
            if (nonImageCount > 0)
            {
                Interlocked.Add(ref completed, nonImageCount);
            }

            var results = new ConcurrentBag<(int PostFileId, SimilarityHashes Hashes)>();

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

                        results.Add((post.PostFileId, hashes));
                        Interlocked.Increment(ref processed);
                        Interlocked.Increment(ref completed);
                        context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref completed);
                        _logger.LogWarning(ex, "Failed to compute similarity hash for post {Id}: {Path}", post.PostId, post.RelativePath);
                        context.Reporter.Update(BuildLiveState("Computing similarity hashes..."));
                    }
                });

            var entityIds = imageBatch.Select(p => p.PostFileId).ToList();
            var entities = await db.PostFiles
                .Where(pf => entityIds.Contains(pf.Id))
                .ToDictionaryAsync(pf => pf.Id, context.CancellationToken);

            foreach (var result in results)
            {
                if (entities.TryGetValue(result.PostFileId, out var entity))
                {
                    entity.PdqHash256 = result.Hashes.PdqHash256;
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
