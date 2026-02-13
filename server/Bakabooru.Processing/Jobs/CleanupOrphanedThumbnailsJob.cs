using Bakabooru.Core.Config;
using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bakabooru.Processing.Jobs;

public class CleanupOrphanedThumbnailsJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupOrphanedThumbnailsJob> _logger;
    private readonly string _thumbnailPath;

    public CleanupOrphanedThumbnailsJob(
        IServiceScopeFactory scopeFactory,
        ILogger<CleanupOrphanedThumbnailsJob> logger,
        IOptions<BakabooruConfig> options,
        IHostEnvironment hostEnvironment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _thumbnailPath = StoragePathResolver.ResolvePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath,
            "../../data/thumbnails");

        if (!Directory.Exists(_thumbnailPath))
        {
            Directory.CreateDirectory(_thumbnailPath);
        }
    }

    public string Name => "Cleanup Orphaned Thumbnails";
    public string Description => "Removes thumbnail files that are not referenced by any post.";

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();

        context.Status.Report("Loading known content hashes...");
        var knownHashes = (await db.Posts
                .AsNoTracking()
                .Where(p => !string.IsNullOrEmpty(p.ContentHash))
                .Select(p => p.ContentHash)
                .Distinct()
                .ToListAsync(context.CancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var thumbnailFiles = Directory
            .EnumerateFiles(_thumbnailPath, "*.jpg", SearchOption.TopDirectoryOnly)
            .ToList();

        if (thumbnailFiles.Count == 0)
        {
            context.Progress.Report(100);
            context.Status.Report("No thumbnails found.");
            return;
        }

        _logger.LogInformation(
            "Checking {ThumbnailCount} thumbnails against {KnownHashCount} known hashes",
            thumbnailFiles.Count,
            knownHashes.Count);

        int deleted = 0;
        int failed = 0;

        for (var index = 0; index < thumbnailFiles.Count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var filePath = thumbnailFiles[index];
            var hash = Path.GetFileNameWithoutExtension(filePath);

            if (!knownHashes.Contains(hash))
            {
                try
                {
                    File.Delete(filePath);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Failed to delete orphaned thumbnail: {Path}", filePath);
                }
            }

            var processed = index + 1;
            if (processed % 50 == 0 || processed == thumbnailFiles.Count)
            {
                context.Progress.Report((float)processed / thumbnailFiles.Count * 100);
                context.Status.Report($"Cleaning thumbnails: {processed}/{thumbnailFiles.Count}");
            }
        }

        context.Progress.Report(100);
        context.Status.Report($"Done — removed {deleted} orphaned thumbnails ({failed} failed)");
        _logger.LogInformation(
            "Orphaned thumbnail cleanup complete: {Deleted} deleted, {Failed} failed, {Total} scanned",
            deleted,
            failed,
            thumbnailFiles.Count);
    }
}
