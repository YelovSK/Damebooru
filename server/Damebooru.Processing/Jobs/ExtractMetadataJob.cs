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

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var query = db.PostFiles.AsNoTracking().AsQueryable();
        if (context.Mode == JobMode.Missing)
        {
            query = query.Where(pf => pf.Width == 0 || string.IsNullOrEmpty(pf.ContentType));
        }

        var totalFiles = await query.CountAsync(context.CancellationToken);

        _logger.LogInformation("Extracting metadata for {Count} files (mode: {Mode})", totalFiles, context.Mode);

        if (totalFiles == 0)
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
            ActivityText = $"Extracting metadata... ({Math.Min(totalFiles, processed + failed)}/{totalFiles})",
            ProgressCurrent = Math.Min(totalFiles, processed + failed),
            ProgressTotal = totalFiles
        };

        var lastId = 0;

        const int batchSize = 100;
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
                .Take(batchSize)
                .ToListAsync(context.CancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].PostFileId;
            var results = new ConcurrentBag<PostFileMetadataResult>();

            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _parallelism,
                    CancellationToken = context.CancellationToken
                },
                async (postFile, ct) =>
                {
                    try
                    {
                        results.Add(await _mediaEnrichmentService.ExtractMetadataAsync(postFile, ct));
                        Interlocked.Increment(ref processed);
                        context.Reporter.Update(BuildLiveState());
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning(ex, "Failed to extract metadata for post file {Id}: {Path}", postFile.PostFileId, postFile.RelativePath);
                        context.Reporter.Update(BuildLiveState());
                    }
                });

            var entityIds = batch.Select(p => p.PostFileId).ToList();
            var entities = await db.PostFiles
                .Where(pf => entityIds.Contains(pf.Id))
                .ToDictionaryAsync(pf => pf.Id, context.CancellationToken);

            foreach (var result in results)
            {
                if (entities.TryGetValue(result.PostFileId, out var entity))
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
            ProgressTotal = totalFiles,
            FinalText = $"Extracted metadata for {processed} files ({failed} failed)."
        });
        _logger.LogInformation("Metadata extraction complete: {Processed} processed, {Failed} failed", processed, failed);
    }
}
