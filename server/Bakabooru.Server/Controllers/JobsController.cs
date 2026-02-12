using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Bakabooru.Server.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly BakabooruDbContext _context;

    public JobsController(IJobService jobService, BakabooruDbContext context)
    {
        _jobService = jobService;
        _context = context;
    }

    [HttpGet]
    public ActionResult<IEnumerable<JobViewDto>> GetJobs()
    {
        var available = _jobService.GetAvailableJobs();
        var activeJobs = _jobService.GetActiveJobs();
        
        // Group by name to handle multiple instances of same job type running (if that ever happens)
        var active = activeJobs
            .GroupBy(j => j.Name)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.StartTime).First(), StringComparer.OrdinalIgnoreCase);

        var result = available.Select(name => new JobViewDto
        {
            Name = name,
            IsRunning = active.ContainsKey(name) && active[name].Status == JobStatus.Running,
            ActiveJobInfo = active.GetValueOrDefault(name)
        });

        return Ok(result);
    }

    [HttpPost("{name}/start")]
    public async Task<ActionResult<StartJobResponseDto>> StartJob(
        string name,
        [FromQuery]
        [RegularExpression("^(missing|all)$", ErrorMessage = "Mode must be either 'missing' or 'all'.")]
        string mode = "missing",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var jobMode = mode.Equals("all", StringComparison.OrdinalIgnoreCase) 
                ? JobMode.All 
                : JobMode.Missing;

            var jobId = await _jobService.StartJobAsync(name, cancellationToken, job => { }, jobMode);
            return Ok(new StartJobResponseDto { JobId = jobId });
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpPost("{id}/cancel")]
    public ActionResult CancelJob(string id)
    {
        _jobService.CancelJob(id);
        return NoContent();
    }

    [HttpGet("history")]
    public async Task<ActionResult<JobHistoryResponseDto>> GetHistory(
        [FromQuery]
        [Range(1, 500)]
        int pageSize = 20,
        [FromQuery]
        [Range(1, int.MaxValue)]
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await _jobService.GetJobHistoryAsync(pageSize, page, cancellationToken);
        return Ok(new JobHistoryResponseDto
        {
            Items = items.Select(i => new JobExecutionDto
            {
                Id = i.Id,
                JobName = i.JobName,
                Status = i.Status,
                StartTime = i.StartTime,
                EndTime = i.EndTime,
                ErrorMessage = i.ErrorMessage
            }).ToList(),
            Total = total
        });
    }

    [HttpGet("schedules")]
    public async Task<ActionResult<IEnumerable<ScheduledJobDto>>> GetSchedules(CancellationToken cancellationToken)
    {
        var schedules = await _context.ScheduledJobs.ToListAsync(cancellationToken);
        return Ok(schedules.Select(s => new ScheduledJobDto
        {
            Id = s.Id,
            JobName = s.JobName,
            CronExpression = s.CronExpression,
            IsEnabled = s.IsEnabled,
            LastRun = s.LastRun,
            NextRun = s.NextRun
        }));
    }

    [HttpPut("schedules/{id}")]
    public async Task<ActionResult<ScheduledJobDto>> UpdateSchedule(int id, [FromBody] ScheduledJobUpdateDto update, CancellationToken cancellationToken)
    {
        var schedule = await _context.ScheduledJobs.FindAsync(new object[] { id }, cancellationToken);
        if (schedule == null) return NotFound();

        // Validate cron expression
        try
        {
            Cronos.CronExpression.Parse(update.CronExpression);
        }
        catch
        {
            return BadRequest($"Invalid cron expression: '{update.CronExpression}'");
        }

        schedule.CronExpression = update.CronExpression;
        schedule.IsEnabled = update.IsEnabled;

        // Always calculate NextRun so the UI shows when it would run
        var cron = Cronos.CronExpression.Parse(schedule.CronExpression);
        schedule.NextRun = cron.GetNextOccurrence(DateTime.UtcNow, inclusive: false);

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new ScheduledJobDto
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
