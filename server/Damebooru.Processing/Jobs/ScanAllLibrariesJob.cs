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

    public async Task<string> ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var syncService = scope.ServiceProvider.GetRequiredService<ILibrarySyncProcessor>();

        var libraries = await dbContext.Libraries.ToListAsync(context.CancellationToken);
        if (libraries.Count == 0)
        {
            return "No libraries configured.";
        }

        var phase = "Scanning libraries...";
        var currentProcessed = 0;

        IProgress<float> progress = new Progress<float>(percent =>
        {
            var normalized = percent <= 1f ? percent * 100f : percent;
            var processed = (int)Math.Clamp(Math.Round(normalized), 0, 100);
            currentProcessed = processed;
            context.Reporter.Report(phase, processed, 100);
        });

        IProgress<string> status = new Progress<string>(message =>
        {
            phase = string.IsNullOrWhiteSpace(message) ? "Scanning libraries..." : message.Trim();
            context.Reporter.Report(phase, currentProcessed, 100);
        });

        context.Reporter.Report(phase, 0, 100);

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

        return $"Scanned {result.Scanned} files: {result.Added} added, {result.Updated} updated, {result.Moved} moved, {result.Removed} removed";
    }
}
