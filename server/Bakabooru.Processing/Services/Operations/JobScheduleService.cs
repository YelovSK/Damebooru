using Bakabooru.Core.DTOs;
using Bakabooru.Core.Interfaces;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class JobScheduleService
{
    private readonly BakabooruDbContext _context;
    private readonly Dictionary<string, int> _jobOrderByKey;
    private readonly Dictionary<string, string> _jobDisplayNameByKey;
    private readonly Dictionary<string, string> _jobKeyByName;

    public JobScheduleService(BakabooruDbContext context, IEnumerable<IJob> jobs)
    {
        _context = context;
        _jobOrderByKey = jobs
            .GroupBy(j => j.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayOrder, StringComparer.OrdinalIgnoreCase);
        _jobDisplayNameByKey = jobs
            .GroupBy(j => j.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        _jobKeyByName = jobs
            .GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
    }

    private string ResolveKey(string storedJobName)
    {
        if (_jobOrderByKey.ContainsKey(storedJobName))
        {
            return storedJobName;
        }

        if (_jobKeyByName.TryGetValue(storedJobName, out var key))
        {
            return key;
        }

        return storedJobName;
    }

    private string ResolveDisplayName(string storedJobName)
    {
        var key = ResolveKey(storedJobName);
        return _jobDisplayNameByKey.TryGetValue(key, out var displayName)
            ? displayName
            : storedJobName;
    }

    public async Task<List<ScheduledJobDto>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _context.ScheduledJobs.ToListAsync(cancellationToken);
        return schedules
            .OrderBy(s => _jobOrderByKey.TryGetValue(ResolveKey(s.JobName), out var order) ? order : int.MaxValue)
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
            var parsed = CronExpression.Parse(expression);
            var nextRuns = new List<DateTime>();
            var cursor = DateTime.UtcNow;

            for (var i = 0; i < Math.Clamp(count, 1, 10); i++)
            {
                var next = parsed.GetNextOccurrence(cursor, inclusive: false);
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
        catch
        {
            return new CronPreviewDto
            {
                IsValid = false,
                Error = $"Invalid cron expression: '{expression}'"
            };
        }
    }
}
