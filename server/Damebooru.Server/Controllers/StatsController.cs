using Damebooru.Core.DTOs;
using Damebooru.Processing.Services;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly StatsReadService _statsReadService;

    public StatsController(StatsReadService statsReadService)
    {
        _statsReadService = statsReadService;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<StatsOverviewDto>> GetOverview(CancellationToken cancellationToken = default)
    {
        return Ok(await _statsReadService.GetOverviewAsync(cancellationToken));
    }

    [HttpGet("growth")]
    public async Task<ActionResult<StatsGrowthDto>> GetGrowth(
        [FromQuery] StatsGrowthDateKind dateKind = StatsGrowthDateKind.Imported,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _statsReadService.GetGrowthAsync(dateKind, cancellationToken));
    }

    [HttpGet("storage")]
    public async Task<ActionResult<StatsStorageDto>> GetStorage(CancellationToken cancellationToken = default)
    {
        return Ok(await _statsReadService.GetStorageAsync(cancellationToken));
    }

    [HttpGet("tags")]
    public async Task<ActionResult<StatsTagsDto>> GetTags(CancellationToken cancellationToken = default)
    {
        return Ok(await _statsReadService.GetTagsAsync(cancellationToken));
    }

    [HttpGet("maintenance")]
    public async Task<ActionResult<StatsMaintenanceDto>> GetMaintenance(CancellationToken cancellationToken = default)
    {
        return Ok(await _statsReadService.GetMaintenanceAsync(cancellationToken));
    }
}
