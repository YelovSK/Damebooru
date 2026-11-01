using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
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

    public SchedulerService(IServiceScopeFactory scopeFactory, ILogger<SchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler Service is starting.");

        await SyncSchedulesAsync(stoppingToken);

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

    /// <summary>
    /// Gives every <see cref="IScheduledJob"/> a schedule, disabled until the user enables it, and drops schedules of any other job.
    /// </summary>
    private async Task SyncSchedulesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var scheduledJobs = scope.ServiceProvider.GetServices<IJob>()
            .OfType<IScheduledJob>()
            .ToDictionary(j => j.Key.Value, StringComparer.OrdinalIgnoreCase);

        var schedules = await dbContext.ScheduledJobs.ToListAsync(cancellationToken);
        foreach (var schedule in schedules.Where(s => !scheduledJobs.ContainsKey(s.JobName)))
        {
            dbContext.ScheduledJobs.Remove(schedule);
            _logger.LogInformation("Removed schedule of unscheduled job: {Key}", schedule.JobName);
        }

        var scheduledKeys = schedules.Select(s => s.JobName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in scheduledJobs.Values.Where(j => !scheduledKeys.Contains(j.Key.Value)))
        {
            dbContext.ScheduledJobs.Add(new ScheduledJob
            {
                JobName = job.Key.Value,
                CronExpression = job.DefaultCron,
                IsEnabled = false,
                NextRun = JobCron.GetNextRun(job.DefaultCron)
            });
            _logger.LogInformation("Added schedule for {Key} ({Cron})", job.Key.Value, job.DefaultCron);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CheckScheduledJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();

        var jobsToRun = await dbContext.ScheduledJobs
            .Where(j => j.IsEnabled && (j.NextRun == null || j.NextRun <= DateTime.UtcNow))
            .ToListAsync(cancellationToken);

        foreach (var scheduledJob in jobsToRun)
        {
            var scheduledKey = JobKey.Parse(scheduledJob.JobName);

            // A run still in progress covers this occurrence; queueing another would start a full run right after it.
            if (jobService.IsRunning(scheduledKey))
            {
                _logger.LogInformation("Skipping scheduled run of {Key}: it is still running", scheduledKey.Value);
                scheduledJob.NextRun = JobCron.GetNextRun(scheduledJob.CronExpression);
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            _logger.LogInformation("Triggering scheduled job: {Key}", scheduledKey.Value);

            try
            {
                await jobService.StartJobAsync(scheduledKey);

                scheduledJob.LastRun = DateTime.UtcNow;
                scheduledJob.NextRun = JobCron.GetNextRun(scheduledJob.CronExpression);

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger scheduled job {Key}", scheduledKey);
            }
        }
    }
}
