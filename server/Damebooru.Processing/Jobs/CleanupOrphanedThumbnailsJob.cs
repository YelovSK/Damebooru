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
    private readonly string _previewPath;
    private readonly string _thumbnailPath;

    public CleanupOrphanedThumbnailsJob(
        IServiceScopeFactory scopeFactory,
        ILogger<CleanupOrphanedThumbnailsJob> logger,
        IOptions<DamebooruConfig> options,
        IHostEnvironment hostEnvironment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _previewPath = MediaPaths.ResolvePreviewStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.PreviewPath);
        _thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath);

        if (!Directory.Exists(_previewPath))
        {
            Directory.CreateDirectory(_previewPath);
        }

        if (!Directory.Exists(_thumbnailPath))
        {
            Directory.CreateDirectory(_thumbnailPath);
        }
    }

    public int DisplayOrder => 60;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Removes thumbnail and preview files that are not referenced by any post file.";
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
        var knownGeneratedFiles = await db.PostFiles
                .AsNoTracking()
                .Where(pf => !string.IsNullOrEmpty(pf.ContentHash))
                .Select(pf => new { pf.LibraryId, pf.ContentHash })
                .Distinct()
                .ToListAsync(context.CancellationToken);

        var knownThumbnailRelativePaths = knownGeneratedFiles
            .Select(file => MediaPaths.GetThumbnailRelativePath(file.LibraryId, file.ContentHash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownPreviewRelativePaths = knownGeneratedFiles
            .Select(file => MediaPaths.GetPreviewRelativePath(file.LibraryId, file.ContentHash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cleanupTargets = new[]
        {
            new CleanupTarget("thumbnails", _thumbnailPath, knownThumbnailRelativePaths),
            new CleanupTarget("previews", _previewPath, knownPreviewRelativePaths),
        };
        var totalFiles = cleanupTargets.Sum(target => target.Files.Count);

        if (totalFiles == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "No thumbnails or previews found."
            });
            return;
        }

        _logger.LogInformation(
            "Checking {GeneratedImageCount} generated images against {KnownThumbnailCount} known thumbnails and {KnownPreviewCount} known previews",
            totalFiles,
            knownThumbnailRelativePaths.Count,
            knownPreviewRelativePaths.Count);

        int deleted = 0;
        int failed = 0;
        var processed = 0;

        foreach (var target in cleanupTargets)
        {
            foreach (var filePath in target.Files)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(target.RootPath, filePath)
                    .Replace('\\', '/');

                if (!target.KnownRelativePaths.Contains(relativePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed to delete orphaned {Kind}: {Path}", target.Kind, filePath);
                    }
                }

                processed++;
                if (processed % 50 == 0 || processed == totalFiles)
                {
                    context.Reporter.Update(new JobState
                    {
                        ActivityText = $"Cleaning orphaned generated images... ({processed}/{totalFiles})",
                        ProgressCurrent = processed,
                        ProgressTotal = totalFiles
                    });
                }
            }
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = totalFiles,
            ProgressTotal = totalFiles,
            FinalText = $"Removed {deleted} orphaned generated images ({failed} failed)."
        });
        _logger.LogInformation(
            "Orphaned generated image cleanup complete: {Deleted} deleted, {Failed} failed, {Total} scanned",
            deleted,
            failed,
            totalFiles);
    }

    private sealed record CleanupTarget(string Kind, string RootPath, HashSet<string> KnownRelativePaths)
    {
        public List<string> Files { get; } = Directory.Exists(RootPath)
            ? Directory.EnumerateFiles(RootPath, MediaPaths.GeneratedImageGlobPattern, SearchOption.AllDirectories).ToList()
            : [];
    }
}
