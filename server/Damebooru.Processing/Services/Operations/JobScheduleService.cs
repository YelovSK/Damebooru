using Cronos;
using Damebooru.Core.DTOs;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class JobScheduleService
{
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

    private string ResolveDisplayName(string storedJobKey)
        => FindJob(storedJobKey)?.Name ?? storedJobKey;

    public async Task<List<ScheduledJobDto>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _context.ScheduledJobs.ToListAsync(cancellationToken);
        return schedules
            .OrderBy(s => FindJob(s.JobName)?.DisplayOrder ?? int.MaxValue)
            .ThenBy(s => s.JobName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ScheduledJobDto
        {
            Id = s.Id,
            JobName = ResolveDisplayName(s.JobName),
            CronExpression = s.CronExpression,
            IsEnabled = s.IsEnabled,
            LastRun = s.LastRun,
            NextRun = s.NextRun
        })
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

        return Result<ScheduledJobDto>.Success(new ScheduledJobDto
        {
            Id = schedule.Id,
            JobName = ResolveDisplayName(schedule.JobName),
            CronExpression = schedule.CronExpression,
            IsEnabled = schedule.IsEnabled,
            LastRun = schedule.LastRun,
            NextRun = schedule.NextRun
        });
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
            var nextRuns = new List<DateTime>();
            var cursor = DateTime.UtcNow;

            for (var i = 0; i < Math.Clamp(count, 1, 10); i++)
            {
                var next = JobCron.GetNextRun(expression, cursor);
                if (!next.HasValue)
                {
                    break;
                }

                nextRuns.Add(next.Value);
                cursor = next.Value;
            }

            return new CronPreviewDto
            {
                IsValid = true,
                NextRuns = nextRuns
            };
        }
        catch (CronFormatException)
        {
            return new CronPreviewDto
            {
                IsValid = false,
                Error = $"Invalid cron expression: '{expression}'"
            };
        }
    }
}
