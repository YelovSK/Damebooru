using Damebooru.Core.DTOs;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Services;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Damebooru.Server.Controllers;

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
            .GroupBy(j => j.Key)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(j => j.Status == JobStatus.Running)
                    .ThenByDescending(j => j.StartTime)
                    .First());

        var result = available.Select(job => new JobViewDto
        {
            Key = job.Key.Value,
            Name = job.Name,
            Description = job.Description,
            SupportsAllMode = job.SupportsAllMode,
            IsRunning = active.TryGetValue(job.Key, out var info) && info.Status == JobStatus.Running,
            ActiveJobInfo = active.TryGetValue(job.Key, out var activeInfo) ? activeInfo : null
        });

        return Ok(result);
    }

    [HttpPost("{key}/start")]
    public async Task<ActionResult<StartJobResponseDto>> StartJob(
        string key,
        [FromQuery]
        [RegularExpression("^(missing|all)$", ErrorMessage = "Mode must be either 'missing' or 'all'.")]
        string mode = "missing")
    {
        try
        {
            var jobMode = mode.Equals("all", StringComparison.OrdinalIgnoreCase) 
                ? JobMode.All 
                : JobMode.Missing;

            if (!JobKey.TryParse(key, out var parsedKey))
            {
                return BadRequest("Invalid job key format.");
            }

            var jobId = await _jobService.StartJobAsync(parsedKey, CancellationToken.None, jobMode);
            return Ok(new StartJobResponseDto { JobId = jobId });
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
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
        var activeByExecutionId = _jobService.GetActiveJobs()
            .Where(j => j.ExecutionId > 0)
            .ToDictionary(j => j.ExecutionId, j => j.State);

        return Ok(new JobHistoryResponseDto
        {
            Items = items.Select(i => new JobExecutionDto
            {
                Id = i.Id,
                JobKey = i.JobKey,
                JobName = i.JobName,
                Status = i.Status,
                StartTime = i.StartTime,
                EndTime = i.EndTime,
                ErrorMessage = i.ErrorMessage,
                State = activeByExecutionId.TryGetValue(i.Id, out var activeState)
                    ? activeState
                    : new JobState
                    {
                        ActivityText = i.ActivityText,
                        FinalText = i.FinalText,
                        ProgressCurrent = i.ProgressCurrent,
                        ProgressTotal = i.ProgressTotal,
                        ResultSchemaVersion = i.ResultSchemaVersion,
                        ResultJson = i.ResultJson,
                    }
            }).ToList(),
            Total = total
        });
    }

    [HttpGet("history/{executionId:int}/result")]
    public async Task<ActionResult<JobResultDto>> GetResult(int executionId, CancellationToken cancellationToken)
    {
        var execution = await _jobService.GetJobExecutionAsync(executionId, cancellationToken);

        if (execution == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(execution.ResultJson))
        {
            return NotFound();
        }

        return Ok(new JobResultDto
        {
            ExecutionId = execution.Id,
            JobKey = execution.JobKey,
            SchemaVersion = execution.ResultSchemaVersion,
            ResultJson = execution.ResultJson,
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
        return await _jobScheduleService.UpdateScheduleAsync(id, update).ToHttpResult(_ => NoContent());
    }

    [HttpGet("cron-preview")]
    public ActionResult<CronPreviewDto> PreviewCron(
        [FromQuery][Required] string expression,
        [FromQuery][Range(1, 10)] int count = 5)
    {
        return Ok(_jobScheduleService.PreviewCron(expression, count));
    }
}
