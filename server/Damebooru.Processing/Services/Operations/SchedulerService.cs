using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Services;

public class SchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SchedulerService> _logger;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default scheduled jobs to seed if they don't exist in the database.
    /// All disabled by default — users can enable and configure from the Jobs page.
    /// </summary>
    private static readonly (JobKey Key, string Cron)[] DefaultJobs =
    [
        (JobKeys.ScanAllLibraries, "0 */6 * * *"),    // Every 6 hours
        (JobKeys.GenerateThumbnails, "30 */6 * * *"),   // 30 min after scan
        (JobKeys.CleanupOrphanedThumbnails, "45 */6 * * *"), // 45 min after scan
        (JobKeys.ExtractMetadata, "35 */6 * * *"),      // 35 min after scan
        (JobKeys.ComputeSimilarity, "40 */6 * * *"),    // 40 min after scan
        (JobKeys.FindDuplicates, "0 3 * * 0"),          // Weekly, Sunday 3 AM
    ];

    public SchedulerService(IServiceScopeFactory scopeFactory, ILogger<SchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler Service is starting.");

        // Seed default scheduled jobs on startup
        await SeedDefaultJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckScheduledJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while checking scheduled jobs.");
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }
    }

    private async Task SeedDefaultJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();

        var availableJobs = jobService.GetAvailableJobs().ToList();
        var availableKeys = availableJobs
            .Select(j => j.Key)
            .ToHashSet();
        var nameToKey = availableJobs
            .GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        var existingSchedules = await dbContext.ScheduledJobs.ToListAsync(cancellationToken);
        var changed = false;

        foreach (var schedule in existingSchedules)
        {
            if (nameToKey.TryGetValue(schedule.JobName, out var resolvedKey)
                && !string.Equals(schedule.JobName, resolvedKey.Value, StringComparison.OrdinalIgnoreCase))
            {
                schedule.JobName = resolvedKey.Value;
                changed = true;
            }
        }

        var existingSet = existingSchedules
            .Select(j => j.JobName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (jobKey, cron) in DefaultJobs)
        {
            if (!availableKeys.Contains(jobKey) || existingSet.Contains(jobKey.Value))
            {
                continue;
            }

            dbContext.ScheduledJobs.Add(new ScheduledJob
            {
                JobName = jobKey.Value,
                CronExpression = cron,
                IsEnabled = false,
                NextRun = CalculateNextRun(cron)
            });
            changed = true;
            _logger.LogInformation("Seeded scheduled job: {Key} ({Cron})", jobKey.Value, cron);
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CheckScheduledJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();
        var availableJobs = jobService.GetAvailableJobs().ToList();
        var keySet = availableJobs
            .Select(j => j.Key)
            .ToHashSet();
        var nameToKey = availableJobs
            .GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        var jobsToRun = await dbContext.ScheduledJobs
            .Where(j => j.IsEnabled && (j.NextRun == null || j.NextRun <= DateTime.UtcNow))
            .ToListAsync(cancellationToken);

        foreach (var scheduledJob in jobsToRun)
        {
            var scheduledKey = JobKey.TryParse(scheduledJob.JobName, out var parsedStoredKey)
                ? parsedStoredKey
                : default;

            if (nameToKey.TryGetValue(scheduledJob.JobName, out var resolvedKey))
            {
                scheduledKey = resolvedKey;
                if (!string.Equals(scheduledJob.JobName, scheduledKey.Value, StringComparison.OrdinalIgnoreCase))
                {
                    scheduledJob.JobName = scheduledKey.Value;
                }
            }

            if (string.IsNullOrEmpty(scheduledKey.Value) || !keySet.Contains(scheduledKey))
            {
                _logger.LogWarning("Skipping unknown scheduled job: {StoredJobName}", scheduledJob.JobName);
                scheduledJob.IsEnabled = false;
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            _logger.LogInformation("Triggering scheduled job: {Key}", scheduledKey.Value);

            try
            {
                await jobService.StartJobAsync(scheduledKey, cancellationToken);

                scheduledJob.LastRun = DateTime.UtcNow;
                scheduledJob.NextRun = CalculateNextRun(scheduledJob.CronExpression);

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger scheduled job {Key}", scheduledKey);
            }
        }
    }

    private DateTime? CalculateNextRun(string cronExpression)
    {
        try
        {
            var expression = CronExpression.Parse(cronExpression);
            var next = expression.GetNextOccurrence(DateTime.UtcNow, inclusive: false);
            return next;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse cron expression '{Cron}', defaulting to 24h", cronExpression);
            return DateTime.UtcNow.AddHours(24);
        }
    }
}
