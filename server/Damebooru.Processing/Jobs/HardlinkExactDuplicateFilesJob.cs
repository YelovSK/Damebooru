using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public sealed class HardlinkExactDuplicateFilesJob : IJob
{
    private sealed record FileCandidate(
        int PostFileId,
        int PostId,
        DateTime ImportDate,
        int LibraryId,
        string LibraryPath,
        string RelativePath,
        string ContentHash,
        string? FileIdentityDevice,
        string? FileIdentityValue);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HardlinkExactDuplicateFilesJob> _logger;
    private readonly IHardLinkService _hardLinkService;
    private readonly IFileIdentityResolver _fileIdentityResolver;

    public static readonly JobKey JobKey = JobKeys.HardlinkExactDuplicateFiles;
    public const string JobName = "Hardlink Exact Duplicate Files";

    public HardlinkExactDuplicateFilesJob(
        IServiceScopeFactory scopeFactory,
        ILogger<HardlinkExactDuplicateFilesJob> logger,
        IHardLinkService hardLinkService,
        IFileIdentityResolver fileIdentityResolver)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _hardLinkService = hardLinkService;
        _fileIdentityResolver = fileIdentityResolver;
    }

    public int DisplayOrder => 46;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Replaces exact duplicate files with hardlinks to one canonical file when the filesystem allows it.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var files = await db.PostFiles
            .AsNoTracking()
            .Where(pf => pf.ContentHash != string.Empty)
            .Select(pf => new FileCandidate(
                pf.Id,
                pf.PostId,
                pf.Post.ImportDate,
                pf.LibraryId,
                pf.Library.Path,
                pf.RelativePath,
                pf.ContentHash,
                pf.FileIdentityDevice,
                pf.FileIdentityValue))
            .ToListAsync(context.CancellationToken);

        var groups = files
            .GroupBy(file => file.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => file.ImportDate)
                .ThenBy(file => file.PostId)
                .ThenBy(file => file.PostFileId)
                .ToList())
            .Where(group => group.Count > 1)
            .ToList();

        var processedGroups = 0;
        var hardlinkedFiles = 0;
        var skippedFiles = 0;
        var failedFiles = 0;

        context.Reporter.Update(new JobState
        {
            ActivityText = $"Hardlinking exact duplicates... (0/{groups.Count})",
            ProgressCurrent = 0,
            ProgressTotal = groups.Count,
        });

        foreach (var group in groups)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var canonical = await ResolveCanonicalCandidateAsync(group, context.CancellationToken);
            if (canonical == null)
            {
                skippedFiles += group.Count;
                processedGroups++;
                continue;
            }

            foreach (var duplicate in group.Where(file => file.PostFileId != canonical.PostFileId))
            {
                var result = await TryHardlinkDuplicateAsync(db, canonical, duplicate, context.CancellationToken);
                switch (result)
                {
                    case HardlinkAttemptResult.Linked:
                        hardlinkedFiles++;
                        break;
                    case HardlinkAttemptResult.Skipped:
                        skippedFiles++;
                        break;
                    case HardlinkAttemptResult.Failed:
                        failedFiles++;
                        break;
                }
            }

            processedGroups++;
            context.Reporter.Update(new JobState
            {
                ActivityText = $"Hardlinking exact duplicates... ({processedGroups}/{groups.Count})",
                ProgressCurrent = processedGroups,
                ProgressTotal = groups.Count,
            });
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = processedGroups,
            ProgressTotal = groups.Count,
            FinalText = $"Hardlinked {hardlinkedFiles} files, skipped {skippedFiles}, failed {failedFiles}."
        });
    }

    private async Task<FileCandidate?> ResolveCanonicalCandidateAsync(List<FileCandidate> group, CancellationToken cancellationToken)
    {
        foreach (var candidate in group)
        {
            if (!SafeSubpathResolver.TryResolve(candidate.LibraryPath, candidate.RelativePath, out var fullPath))
            {
                continue;
            }

            if (!File.Exists(fullPath))
            {
                continue;
            }

            return candidate;
        }

        await Task.CompletedTask;
        return null;
    }

    private async Task<HardlinkAttemptResult> TryHardlinkDuplicateAsync(
        DamebooruDbContext db,
        FileCandidate canonical,
        FileCandidate duplicate,
        CancellationToken cancellationToken)
    {
        if (!SafeSubpathResolver.TryResolve(canonical.LibraryPath, canonical.RelativePath, out var canonicalPath)
            || !SafeSubpathResolver.TryResolve(duplicate.LibraryPath, duplicate.RelativePath, out var duplicatePath))
        {
            return HardlinkAttemptResult.Skipped;
        }

        if (!File.Exists(canonicalPath) || !File.Exists(duplicatePath))
        {
            return HardlinkAttemptResult.Skipped;
        }

        var canonicalIdentity = _fileIdentityResolver.TryResolve(canonicalPath);
        var duplicateIdentity = _fileIdentityResolver.TryResolve(duplicatePath);
        if (canonicalIdentity == null || duplicateIdentity == null)
        {
            return HardlinkAttemptResult.Skipped;
        }

        if (!string.Equals(canonicalIdentity.Device, duplicateIdentity.Device, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Skipping hardlink for {DuplicatePath}: canonical file is on a different device/volume.",
                duplicate.RelativePath);
            return HardlinkAttemptResult.Skipped;
        }

        if (string.Equals(canonicalIdentity.Value, duplicateIdentity.Value, StringComparison.OrdinalIgnoreCase))
        {
            return HardlinkAttemptResult.Skipped;
        }

        var linkResult = _hardLinkService.ReplaceWithHardLink(duplicatePath, canonicalPath);
        if (!linkResult.Success)
        {
            _logger.LogWarning(
                "Failed to hardlink duplicate file {DuplicatePath} -> {CanonicalPath}: {Reason}",
                duplicate.RelativePath,
                canonical.RelativePath,
                linkResult.FailureReason);
            return HardlinkAttemptResult.Failed;
        }

        var refreshedIdentity = _fileIdentityResolver.TryResolve(duplicatePath);
        var refreshedMtime = File.GetLastWriteTimeUtc(duplicatePath);

        await db.PostFiles
            .Where(pf => pf.Id == duplicate.PostFileId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(pf => pf.FileIdentityDevice, refreshedIdentity?.Device)
                .SetProperty(pf => pf.FileIdentityValue, refreshedIdentity?.Value)
                .SetProperty(pf => pf.FileModifiedDate, refreshedMtime), cancellationToken);

        return HardlinkAttemptResult.Linked;
    }

    private enum HardlinkAttemptResult
    {
        Linked,
        Skipped,
        Failed,
    }
}
