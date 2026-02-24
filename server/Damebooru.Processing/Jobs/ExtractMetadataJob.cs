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

public class ExtractMetadataJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ExtractMetadata;
    public const string JobName = "Extract Metadata";

    private sealed record PostMetadataCandidate(int Id, string RelativePath, string LibraryPath);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExtractMetadataJob> _logger;
    private readonly int _parallelism;

    public ExtractMetadataJob(
        IServiceScopeFactory scopeFactory,
        ILogger<ExtractMetadataJob> logger,
        IOptions<DamebooruConfig> config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _parallelism = Math.Max(1, config.Value.Processing.MetadataParallelism);
    }

    public int DisplayOrder => 20;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Extracts dimensions and content type for posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var mediaFileProcessor = scope.ServiceProvider.GetRequiredService<IMediaFileProcessor>();

        // In "missing" mode, find posts that haven't been processed yet (Width == 0)
        var query = db.Posts.AsNoTracking().AsQueryable();
        if (context.Mode == JobMode.Missing)
        {
            query = query.Where(p => p.Width == 0 || string.IsNullOrEmpty(p.ContentType));
        }

        var totalPosts = await query.CountAsync(context.CancellationToken);

        _logger.LogInformation("Extracting metadata for {Count} posts (mode: {Mode})", totalPosts, context.Mode);

        if (totalPosts == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "All metadata is up to date."
            });
            return;
        }

        int processed = 0;
        int failed = 0;

        JobState BuildLiveState() => new()
        {
            ActivityText = $"Extracting metadata... ({Math.Min(totalPosts, processed + failed)}/{totalPosts})",
            ProgressCurrent = Math.Min(totalPosts, processed + failed),
            ProgressTotal = totalPosts
        };

        var lastId = 0;

        // Process in bounded batches to keep memory usage stable regardless of DB size.
        const int batchSize = 100;
        while (true)
        {
            var batch = await query
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => new PostMetadataCandidate(p.Id, p.RelativePath, p.Library.Path))
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].Id;
            var results = new ConcurrentBag<(int PostId, int Width, int Height, string ContentType)>();

            await Parallel.ForEachAsync(
                batch,
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
                        var metadata = await mediaFileProcessor.GetMetadataAsync(fullPath, ct);
                        if (metadata.Width <= 0 || metadata.Height <= 0)
                        {
                            Interlocked.Increment(ref failed);
                            _logger.LogWarning(
                                "Metadata extraction produced invalid dimensions for post {Id}: {Path} ({Width}x{Height})",
                                post.Id,
                                post.RelativePath,
                                metadata.Width,
                                metadata.Height);
                            return;
                        }

                        var contentType = SupportedMedia.GetMimeType(Path.GetExtension(post.RelativePath));

                        results.Add((post.Id, metadata.Width, metadata.Height, contentType));
                        Interlocked.Increment(ref processed);
                        context.Reporter.Update(BuildLiveState());
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to extract metadata for post {Id}: {Path}", post.Id, post.RelativePath);
                        context.Reporter.Update(BuildLiveState());
                    }
                });

            var entityIds = batch.Select(p => p.Id).ToList();
            var entities = await db.Posts
                .Where(p => entityIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, context.CancellationToken);

            foreach (var result in results)
            {
                if (entities.TryGetValue(result.PostId, out var entity))
                {
                    entity.Width = result.Width;
                    entity.Height = result.Height;
                    entity.ContentType = result.ContentType;
                }
            }

            await db.SaveChangesAsync(context.CancellationToken);

            context.Reporter.Update(BuildLiveState());
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = processed + failed,
            ProgressTotal = totalPosts,
            FinalText = $"Extracted metadata for {processed} posts ({failed} failed)."
        });
        _logger.LogInformation("Metadata extraction complete: {Processed} processed, {Failed} failed", processed, failed);
    }
}
