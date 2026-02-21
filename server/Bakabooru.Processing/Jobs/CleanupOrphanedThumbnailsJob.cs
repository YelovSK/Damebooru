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

public class CleanupOrphanedThumbnailsJob : IJob
{
    public const string JobKey = "cleanup-orphaned-thumbnails";
    public const string JobName = "Cleanup Orphaned Thumbnails";

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
        _thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath);

        if (!Directory.Exists(_thumbnailPath))
        {
            Directory.CreateDirectory(_thumbnailPath);
        }
    }

    public int DisplayOrder => 60;
    public string Key => JobKey;
    public string Name => JobName;
    public string Description => "Removes thumbnail files that are not referenced by any post.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();

        context.State.Report(new JobState
        {
            Phase = "Loading known content hashes..."
        });
        var knownThumbnailRelativePaths = (await db.Posts
                .AsNoTracking()
                .Where(p => !string.IsNullOrEmpty(p.ContentHash))
                .Select(p => MediaPaths.GetThumbnailRelativePath(p.LibraryId, p.ContentHash))
                .Distinct()
                .ToListAsync(context.CancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var thumbnailFiles = Directory
            .EnumerateFiles(_thumbnailPath, MediaPaths.ThumbnailGlobPattern, SearchOption.AllDirectories)
            .ToList();

        if (thumbnailFiles.Count == 0)
        {
            context.State.Report(new JobState
            {
                Phase = "Completed",
                Processed = 0,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Summary = "No thumbnails found."
            });
            return;
        }

        _logger.LogInformation(
            "Checking {ThumbnailCount} thumbnails against {KnownThumbnailCount} known thumbnails",
            thumbnailFiles.Count,
            knownThumbnailRelativePaths.Count);

        int deleted = 0;
        int failed = 0;

        for (var index = 0; index < thumbnailFiles.Count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var filePath = thumbnailFiles[index];
            var relativePath = Path.GetRelativePath(_thumbnailPath, filePath)
                .Replace('\\', '/');

            if (!knownThumbnailRelativePaths.Contains(relativePath))
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
                context.State.Report(new JobState
                {
                    Phase = "Cleaning orphaned thumbnails...",
                    Processed = processed,
                    Total = thumbnailFiles.Count,
                    Succeeded = deleted,
                    Failed = failed,
                    Summary = $"Deleted {deleted} orphaned thumbnails ({failed} failed)"
                });
            }
        }

        context.State.Report(new JobState
        {
            Phase = "Completed",
            Processed = thumbnailFiles.Count,
            Total = thumbnailFiles.Count,
            Succeeded = deleted,
            Failed = failed,
            Summary = $"Removed {deleted} orphaned thumbnails ({failed} failed)."
        });
        _logger.LogInformation(
            "Orphaned thumbnail cleanup complete: {Deleted} deleted, {Failed} failed, {Total} scanned",
            deleted,
            failed,
            thumbnailFiles.Count);
    }
}
