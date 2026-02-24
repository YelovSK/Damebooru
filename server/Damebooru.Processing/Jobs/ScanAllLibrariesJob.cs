using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Damebooru.Processing.Jobs;

public class ScanAllLibrariesJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ScanAllLibraries;
    public const string JobName = "Scan All Libraries";

    private readonly IServiceScopeFactory _scopeFactory;

    public ScanAllLibrariesJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public int DisplayOrder => 10;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Triggers a recursive scan for all configured libraries.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var syncService = scope.ServiceProvider.GetRequiredService<ILibrarySyncProcessor>();

        var libraries = await dbContext.Libraries.ToListAsync(context.CancellationToken);
        if (libraries.Count == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 100,
                ProgressTotal = 100,
                FinalText = "No libraries configured."
            });
            return;
        }

        var phase = "Scanning libraries...";
        var currentProcessed = 0;

        static string WithProgress(string phaseText, int processed)
            => $"{phaseText} ({processed}/100)";

        IProgress<float> progress = new Progress<float>(percent =>
        {
            var normalized = percent <= 1f ? percent * 100f : percent;
            var processed = (int)Math.Clamp(Math.Round(normalized), 0, 100);
            currentProcessed = processed;
            context.Reporter.Update(new JobState
            {
                ActivityText = WithProgress(phase, processed),
                ProgressCurrent = processed,
                ProgressTotal = 100
            });
        });

        IProgress<string> status = new Progress<string>(message =>
        {
            phase = string.IsNullOrWhiteSpace(message) ? "Scanning libraries..." : message.Trim();
            context.Reporter.Update(new JobState
            {
                ActivityText = WithProgress(phase, currentProcessed),
                ProgressCurrent = currentProcessed,
                ProgressTotal = 100
            });
        });

        context.Reporter.Update(new JobState
        {
            ActivityText = WithProgress(phase, 0),
            ProgressCurrent = 0,
            ProgressTotal = 100
        });

        var totalLibraries = libraries.Count;
        var libraryIndex = 0;
        var result = ScanResult.Empty;

        foreach (var library in libraries)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            var displayName = string.IsNullOrWhiteSpace(library.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(library.Path))
                : library.Name;
            phase = $"Scanning library: {displayName}";
            status.Report(phase);

            var subProgress = new Progress<float>(percent =>
            {
                var baseProgress = (float)libraryIndex / totalLibraries * 100;
                var slice = 100f / totalLibraries;
                progress.Report(baseProgress + (percent / 100f * slice));
            });

            result += await syncService.ProcessDirectoryAsync(
                library,
                library.Path,
                subProgress,
                status,
                context.CancellationToken);

            libraryIndex++;
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = 100,
            ProgressTotal = 100,
            FinalText = $"Scanned {result.Scanned} files: {result.Added} added, {result.Updated} updated, {result.Moved} moved, {result.Removed} removed"
        });
    }
}
