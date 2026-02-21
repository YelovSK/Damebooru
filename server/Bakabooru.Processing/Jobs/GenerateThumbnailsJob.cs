using Bakabooru.Core;
using Bakabooru.Core.Config;
using Bakabooru.Core.Interfaces;
using Bakabooru.Core.Paths;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bakabooru.Processing.Jobs;

public class GenerateThumbnailsJob : IJob
{
    public const string JobKey = "generate-thumbnails";
    public const string JobName = "Generate Thumbnails";

    private sealed record ThumbnailCandidate(int Id, int LibraryId, string ContentHash, string RelativePath, string LibraryPath);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GenerateThumbnailsJob> _logger;
    private readonly string _thumbnailPath;
    private readonly int _parallelism;

    public GenerateThumbnailsJob(
        IServiceScopeFactory scopeFactory,
        ILogger<GenerateThumbnailsJob> logger,
        IOptions<BakabooruConfig> options,
        IHostEnvironment hostEnvironment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _parallelism = Math.Max(1, options.Value.Processing.ThumbnailParallelism);
        _thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath);

        if (!Directory.Exists(_thumbnailPath))
            Directory.CreateDirectory(_thumbnailPath);
    }

    public int DisplayOrder => 50;
    public string Key => JobKey;
    public string Name => JobName;
    public string Description => "Generates missing (or all) thumbnails for posts.";
    public bool SupportsAllMode => true;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        var mediaFileProcessor = scope.ServiceProvider.GetRequiredService<IMediaFileProcessor>();

        var query = db.Posts
            .AsNoTracking()
            .Where(p => !string.IsNullOrEmpty(p.ContentHash))
            .AsQueryable();

        var totalCandidates = await query.CountAsync(context.CancellationToken);
        _logger.LogInformation(
            "Generating thumbnails for up to {Count} posts (mode: {Mode})",
            totalCandidates,
            context.Mode);

        if (totalCandidates == 0)
        {
            context.State.Report(new JobState
            {
                Phase = "Completed",
                Processed = 0,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Skipped = 0,
                Summary = "All thumbnails are up to date."
            });
            return;
        }

        var scanned = 0;
        int processed = 0;
        int failed = 0;
        int skipped = 0;
        int lastId = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _parallelism,
            CancellationToken = context.CancellationToken
        };

        const int batchSize = 20;
        while (true)
        {
            var batch = await query
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Select(p => new ThumbnailCandidate(p.Id, p.LibraryId, p.ContentHash!, p.RelativePath, p.Library.Path))
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].Id;
            scanned += batch.Count;

            List<ThumbnailCandidate> toProcess;
            if (context.Mode == JobMode.All)
            {
                toProcess = batch;
            }
            else
            {
                toProcess = batch
                    .Where(p => !File.Exists(MediaPaths.GetThumbnailFilePath(_thumbnailPath, p.LibraryId, p.ContentHash)))
                    .ToList();

                skipped += batch.Count - toProcess.Count;
            }

            await Parallel.ForEachAsync(toProcess, parallelOptions, async (post, ct) =>
            {
                try
                {
                    var fullPath = Path.Combine(post.LibraryPath, post.RelativePath);
                    var destination = MediaPaths.GetThumbnailFilePath(_thumbnailPath, post.LibraryId, post.ContentHash);
                    var destinationDirectory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }
                    await mediaFileProcessor.GenerateThumbnailAsync(fullPath, destination, 400, ct);
                    Interlocked.Increment(ref processed);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "Failed to generate thumbnail for post {Id}: {Path}", post.Id, post.RelativePath);
                }
            });

            context.State.Report(new JobState
            {
                Phase = "Generating thumbnails...",
                Processed = scanned,
                Total = totalCandidates,
                Succeeded = processed,
                Failed = failed,
                Skipped = skipped,
                Summary = $"Generated {processed}, skipped {skipped}, failed {failed}"
            });
        }

        context.State.Report(new JobState
        {
            Phase = "Completed",
            Processed = scanned,
            Total = totalCandidates,
            Succeeded = processed,
            Failed = failed,
            Skipped = skipped,
            Summary = $"Generated {processed} thumbnails ({failed} failed, {skipped} skipped)."
        });
        _logger.LogInformation(
            "Thumbnail generation complete: {Processed} generated, {Failed} failed, {Skipped} skipped",
            processed,
            failed,
            skipped);
    }
}
