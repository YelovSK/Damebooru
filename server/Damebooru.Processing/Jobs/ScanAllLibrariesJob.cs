using Damebooru.Core.Interfaces;
using Damebooru.Processing.Scanning;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

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
        var scannerService = scope.ServiceProvider.GetRequiredService<IScannerService>();

        var phase = "Scanning libraries...";
        var currentProcessed = 0;

        var progress = new Progress<float>(percent =>
        {
            var normalized = percent <= 1f ? percent * 100f : percent;
            var processed = (int)Math.Clamp(Math.Round(normalized), 0, 100);
            currentProcessed = processed;
            context.Reporter.Update(new JobState
            {
                ActivityText = phase,
                ProgressCurrent = processed,
                ProgressTotal = 100
            });
        });

        var status = new Progress<string>(message =>
        {
            phase = string.IsNullOrWhiteSpace(message) ? "Scanning libraries..." : message.Trim();
            context.Reporter.Update(new JobState
            {
                ActivityText = phase,
                ProgressCurrent = currentProcessed,
                ProgressTotal = 100
            });
        });

        context.Reporter.Update(new JobState
        {
            ActivityText = phase,
            ProgressCurrent = 0,
            ProgressTotal = 100
        });

        var result = await scannerService.ScanAllLibrariesAsync(progress, status, context.CancellationToken);

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = 100,
            ProgressTotal = 100,
            FinalText = $"Scanned {result.Scanned} files: {result.Added} added, {result.Updated} updated, {result.Moved} moved, {result.Removed} removed",
            ResultSchemaVersion = 1,
            ResultJson = JsonSerializer.Serialize(new
            {
                scanned = result.Scanned,
                added = result.Added,
                updated = result.Updated,
                moved = result.Moved,
                removed = result.Removed,
            })
        });
    }
}
