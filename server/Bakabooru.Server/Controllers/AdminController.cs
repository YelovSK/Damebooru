using Bakabooru.Core.Interfaces;
using Bakabooru.Processing.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly IJobService _jobService;

    public AdminController(IJobService jobService)
    {
        _jobService = jobService;
    }

    [HttpGet("jobs")]
    public ActionResult<IEnumerable<JobInfo>> GetJobs()
    {
        return Ok(_jobService.GetActiveJobs());
    }

    [HttpPost("jobs/scan-all")]
    public async Task<IActionResult> ScanAll()
    {
        try
        {
            var jobId = await _jobService.StartJobAsync(ScanAllLibrariesJob.JobKey, CancellationToken.None);
            return Accepted(new { JobId = jobId });
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpDelete("jobs/{jobId}")]
    public IActionResult CancelJob(string jobId)
    {
        _jobService.CancelJob(jobId);
        return NoContent();
    }
}
