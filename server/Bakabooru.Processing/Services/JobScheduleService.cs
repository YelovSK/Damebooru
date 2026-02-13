using Bakabooru.Core.DTOs;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class JobScheduleService
{
    private readonly BakabooruDbContext _context;

    public JobScheduleService(BakabooruDbContext context)
    {
        _context = context;
    }

    public async Task<List<ScheduledJobDto>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _context.ScheduledJobs.ToListAsync(cancellationToken);
        return schedules.Select(s => new ScheduledJobDto
        {
            Id = s.Id,
            JobName = s.JobName,
            CronExpression = s.CronExpression,
            IsEnabled = s.IsEnabled,
            LastRun = s.LastRun,
            NextRun = s.NextRun
        }).ToList();
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
            Cronos.CronExpression.Parse(update.CronExpression);
        }
        catch
        {
            return Result<ScheduledJobDto>.Failure(OperationError.InvalidInput, $"Invalid cron expression: '{update.CronExpression}'");
        }

        schedule.CronExpression = update.CronExpression;
        schedule.IsEnabled = update.IsEnabled;

        var cron = Cronos.CronExpression.Parse(schedule.CronExpression);
        schedule.NextRun = cron.GetNextOccurrence(DateTime.UtcNow, inclusive: false);

        await _context.SaveChangesAsync();

        return Result<ScheduledJobDto>.Success(new ScheduledJobDto
        {
            Id = schedule.Id,
            JobName = schedule.JobName,
            CronExpression = schedule.CronExpression,
            IsEnabled = schedule.IsEnabled,
            LastRun = schedule.LastRun,
            NextRun = schedule.NextRun
        });
    }
}

