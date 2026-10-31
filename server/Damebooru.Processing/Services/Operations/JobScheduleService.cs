using Cronos;
using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class JobScheduleService
{
    private const int UpcomingRunCount = 3;

    private readonly DamebooruDbContext _context;
    private readonly Dictionary<JobKey, IJob> _jobsByKey;

    public JobScheduleService(DamebooruDbContext context, IEnumerable<IJob> jobs)
    {
        _context = context;
        _jobsByKey = jobs
            .GroupBy(j => j.Key)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private IJob? FindJob(string storedJobKey)
        => JobKey.TryParse(storedJobKey, out var key) && _jobsByKey.TryGetValue(key, out var job) ? job : null;

    public async Task<List<ScheduledJobDto>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _context.ScheduledJobs.ToListAsync(cancellationToken);
        return schedules
            .OrderBy(s => FindJob(s.JobName)?.DisplayOrder ?? int.MaxValue)
            .ThenBy(s => s.JobName, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    public async Task<Result<ScheduledJobDto>> UpdateScheduleAsync(int id, ScheduledJobUpdateDto update)
    {
        var schedule = await _context.ScheduledJobs.FindAsync(new object[] { id });
        if (schedule == null)
        {
            return Result<ScheduledJobDto>.Failure(OperationError.NotFound, "Schedule not found.");
        }

        try
        {
            schedule.NextRun = JobCron.GetNextRun(update.CronExpression);
        }
        catch (CronFormatException)
        {
            return Result<ScheduledJobDto>.Failure(OperationError.InvalidInput, $"Invalid cron expression: '{update.CronExpression}'");
        }

        schedule.CronExpression = update.CronExpression;
        schedule.IsEnabled = update.IsEnabled;

        await _context.SaveChangesAsync();

        return Result<ScheduledJobDto>.Success(ToDto(schedule));
    }

    private ScheduledJobDto ToDto(ScheduledJob schedule)
    {
        var job = FindJob(schedule.JobName);
        return new ScheduledJobDto
        {
            Id = schedule.Id,
            JobKey = job?.Key.Value ?? schedule.JobName,
            JobName = job?.Name ?? schedule.JobName,
            CronExpression = schedule.CronExpression,
            IsEnabled = schedule.IsEnabled,
            LastRun = schedule.LastRun,
            NextRun = schedule.NextRun,
            UpcomingRuns = JobCron.GetUpcomingRuns(schedule.CronExpression, UpcomingRunCount)
        };
    }

    public CronPreviewDto PreviewCron(string cronExpression, int count = 5)
    {
        var expression = cronExpression?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new CronPreviewDto
            {
                IsValid = false,
                Error = "Cron expression is required."
            };
        }

        try
        {
            return new CronPreviewDto
            {
                IsValid = true,
                NextRuns = JobCron.GetUpcomingRuns(expression, Math.Clamp(count, 1, 10))
            };
        }
        catch (CronFormatException)
        {
            return new CronPreviewDto
            {
                IsValid = false,
                Error = "Invalid cron expression"
            };
        }
    }
}
