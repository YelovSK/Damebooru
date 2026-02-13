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

public class ExtractMetadataJob : IJob
{
    private sealed record PostMetadataCandidate(int Id, string RelativePath, string LibraryPath);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExtractMetadataJob> _logger;
    private readonly int _parallelism;

    public ExtractMetadataJob(
        IServiceScopeFactory scopeFactory,
        ILogger<ExtractMetadataJob> logger,
        IOptions<BakabooruConfig> config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _parallelism = Math.Max(1, config.Value.Processing.MetadataParallelism);
    }

    public string Name => "Extract Metadata";
    public string Description => "Extracts dimensions and content type for posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        var imageProcessor = scope.ServiceProvider.GetRequiredService<IImageProcessor>();

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
            context.State.Report(new JobState
            {
                Phase = "Completed",
                Processed = 0,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Summary = "All metadata is up to date."
            });
            return;
        }

        int processed = 0;
        int failed = 0;

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
                        var metadata = await imageProcessor.GetMetadataAsync(fullPath, ct);
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

                        var contentType = metadata.ContentType ?? SupportedMedia.GetMimeType(Path.GetExtension(post.RelativePath));

                        results.Add((post.Id, metadata.Width, metadata.Height, contentType));
                        Interlocked.Increment(ref processed);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to extract metadata for post {Id}: {Path}", post.Id, post.RelativePath);
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

            var total = processed + failed;
            context.State.Report(new JobState
            {
                Phase = "Extracting metadata...",
                Processed = total,
                Total = totalPosts,
                Succeeded = processed,
                Failed = failed,
                Summary = $"Processed {total}/{totalPosts}"
            });
        }

        context.State.Report(new JobState
        {
            Phase = "Completed",
            Processed = processed + failed,
            Total = totalPosts,
            Succeeded = processed,
            Failed = failed,
            Summary = $"Extracted metadata for {processed} posts ({failed} failed)."
        });
        _logger.LogInformation("Metadata extraction complete: {Processed} processed, {Failed} failed", processed, failed);
    }
}
