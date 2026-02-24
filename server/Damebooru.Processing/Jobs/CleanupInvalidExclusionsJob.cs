using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public class CleanupInvalidExclusionsJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.CleanupInvalidExclusions;
    public const string JobName = "Cleanup Invalid Exclusions";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupInvalidExclusionsJob> _logger;
    private readonly IHasherService _hasher;
    private readonly string _contentRootPath;

    private sealed record ExclusionCandidate(int Id, int LibraryId, string LibraryPath, string RelativePath, string ContentHash);

    public CleanupInvalidExclusionsJob(
        IServiceScopeFactory scopeFactory,
        ILogger<CleanupInvalidExclusionsJob> logger,
        IHasherService hasher,
        IHostEnvironment hostEnvironment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _hasher = hasher;
        _contentRootPath = hostEnvironment.ContentRootPath;
    }

    public int DisplayOrder => 65;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Removes exclusions for missing files or hash mismatches.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var candidates = await db.ExcludedFiles
            .AsNoTracking()
            .Select(e => new ExclusionCandidate(
                e.Id,
                e.LibraryId,
                e.Library.Path,
                e.RelativePath,
                e.ContentHash))
            .ToListAsync(context.CancellationToken);

        if (candidates.Count == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "No exclusions found."
            });
            return;
        }

        var idsToRemove = new List<int>(capacity: Math.Min(candidates.Count, 1024));
        var removedMissing = 0;
        var removedHashMismatch = 0;
        var failed = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var entry = candidates[index];
            try
            {
                if (!TryResolveSafeFullPath(entry.LibraryPath, entry.RelativePath, out var fullPath))
                {
                    idsToRemove.Add(entry.Id);
                    removedMissing++;
                }
                else if (!File.Exists(fullPath))
                {
                    idsToRemove.Add(entry.Id);
                    removedMissing++;
                }
                else
                {
                    var currentHash = await _hasher.ComputeContentHashAsync(fullPath, context.CancellationToken);
                    if (string.IsNullOrWhiteSpace(currentHash)
                        || !string.Equals(currentHash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        idsToRemove.Add(entry.Id);
                        removedHashMismatch++;
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to validate exclusion {Id} ({Path})", entry.Id, entry.RelativePath);
            }

            var processed = index + 1;
            if (processed % 25 == 0 || processed == candidates.Count)
            {
                context.Reporter.Update(new JobState
                {
                    ActivityText = $"Validating exclusions... ({processed}/{candidates.Count})",
                    ProgressCurrent = processed,
                    ProgressTotal = candidates.Count,
                });
            }
        }

        if (idsToRemove.Count > 0)
        {
            const int batchSize = 500;
            for (var i = 0; i < idsToRemove.Count; i += batchSize)
            {
                var batch = idsToRemove.Skip(i).Take(batchSize).ToList();
                await db.ExcludedFiles
                    .Where(e => batch.Contains(e.Id))
                    .ExecuteDeleteAsync(context.CancellationToken);
            }
        }

        var removedTotal = removedMissing + removedHashMismatch;

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = candidates.Count,
            ProgressTotal = candidates.Count,
            FinalText = $"Removed {removedTotal} invalid exclusions ({failed} failed validations).",
        });

        _logger.LogInformation(
            "Invalid exclusion cleanup complete: {Scanned} scanned, {RemovedTotal} removed ({RemovedMissing} missing, {RemovedHashMismatch} hash mismatch), {Failed} failed",
            candidates.Count,
            removedTotal,
            removedMissing,
            removedHashMismatch,
            failed);
    }

    private bool TryResolveSafeFullPath(string libraryPath, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        var absoluteLibraryPath = Path.IsPathRooted(libraryPath)
            ? libraryPath
            : Path.Combine(_contentRootPath, libraryPath);

        if (!SafeSubpathResolver.TryResolve(absoluteLibraryPath, relativePath, out var candidatePath))
        {
            return false;
        }

        fullPath = candidatePath;
        return true;
    }
}
