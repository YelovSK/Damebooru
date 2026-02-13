using Bakabooru.Core.DTOs;
using Bakabooru.Core.Interfaces;
using Bakabooru.Processing.Services;
using Bakabooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly JobScheduleService _jobScheduleService;

    public JobsController(IJobService jobService, JobScheduleService jobScheduleService)
    {
        _jobService = jobService;
        _jobScheduleService = jobScheduleService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<JobViewDto>> GetJobs()
    {
        var available = _jobService.GetAvailableJobs();
        var activeJobs = _jobService.GetActiveJobs();

        // Prefer currently running entries if there are multiple active records with same name.
        var active = activeJobs
            .GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(j => j.Status == JobStatus.Running)
                    .ThenByDescending(j => j.StartTime)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var result = available.Select(name => new JobViewDto
        {
            Name = name,
            IsRunning = active.TryGetValue(name, out var info) && info.Status == JobStatus.Running,
            ActiveJobInfo = active.TryGetValue(name, out var activeInfo) ? activeInfo : null
        });

        return Ok(result);
    }

    [HttpPost("{name}/start")]
    public async Task<ActionResult<StartJobResponseDto>> StartJob(
        string name,
        [FromQuery]
        [RegularExpression("^(missing|all)$", ErrorMessage = "Mode must be either 'missing' or 'all'.")]
        string mode = "missing")
    {
        try
        {
            var jobMode = mode.Equals("all", StringComparison.OrdinalIgnoreCase) 
                ? JobMode.All 
                : JobMode.Missing;

            var jobId = await _jobService.StartJobAsync(name, CancellationToken.None, job => { }, jobMode);
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
        return Ok(await _jobScheduleService.GetSchedulesAsync(cancellationToken));
    }

    [HttpPut("schedules/{id}")]
    public async Task<IActionResult> UpdateSchedule(int id, [FromBody] ScheduledJobUpdateDto update)
    {
        return await _jobScheduleService.UpdateScheduleAsync(id, update).ToHttpResult();
    }
}
