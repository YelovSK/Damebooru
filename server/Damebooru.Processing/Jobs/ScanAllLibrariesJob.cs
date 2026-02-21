using Damebooru.Core.Interfaces;
using Damebooru.Processing.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace Damebooru.Processing.Jobs;

public class ScanAllLibrariesJob : IJob
{
    public const string JobKey = "scan-all-libraries";
    public const string JobName = "Scan All Libraries";

    private readonly IServiceScopeFactory _scopeFactory;

    public ScanAllLibrariesJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public int DisplayOrder => 10;
    public string Key => JobKey;
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
            context.State.Report(new JobState
            {
                Phase = phase,
                Processed = processed,
                Total = 100
            });
        });

        var status = new Progress<string>(message =>
        {
            phase = string.IsNullOrWhiteSpace(message) ? "Scanning libraries..." : message.Trim();
            context.State.Report(new JobState
            {
                Phase = phase,
                Processed = currentProcessed,
                Total = 100
            });
        });

        context.State.Report(new JobState
        {
            Phase = phase,
            Processed = 0,
            Total = 100
        });

        var result = await scannerService.ScanAllLibrariesAsync(progress, status, context.CancellationToken);

        context.State.Report(new JobState
        {
            Phase = "Completed",
            Processed = 100,
            Total = 100,
            Summary = $"Scanned {result.Scanned} files: {result.Added} added, {result.Updated} updated, {result.Moved} moved, {result.Removed} removed"
        });
    }
}
