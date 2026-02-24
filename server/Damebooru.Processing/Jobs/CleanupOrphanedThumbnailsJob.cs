using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Jobs;

public class CleanupOrphanedThumbnailsJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.CleanupOrphanedThumbnails;
    public const string JobName = "Cleanup Orphaned Thumbnails";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupOrphanedThumbnailsJob> _logger;
    private readonly string _thumbnailPath;

    public CleanupOrphanedThumbnailsJob(
        IServiceScopeFactory scopeFactory,
        ILogger<CleanupOrphanedThumbnailsJob> logger,
        IOptions<DamebooruConfig> options,
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
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Removes thumbnail files that are not referenced by any post.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        context.Reporter.Update(new JobState
        {
            ActivityText = "Loading known content hashes...",
            ProgressCurrent = null,
            ProgressTotal = null,
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
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
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "No thumbnails found."
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
                context.Reporter.Update(new JobState
                {
                    ActivityText = $"Cleaning orphaned thumbnails... ({processed}/{thumbnailFiles.Count})",
                    ProgressCurrent = processed,
                    ProgressTotal = thumbnailFiles.Count
                });
            }
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = thumbnailFiles.Count,
            ProgressTotal = thumbnailFiles.Count,
            FinalText = $"Removed {deleted} orphaned thumbnails ({failed} failed)."
        });
        _logger.LogInformation(
            "Orphaned thumbnail cleanup complete: {Deleted} deleted, {Failed} failed, {Total} scanned",
            deleted,
            failed,
            thumbnailFiles.Count);
    }
}
