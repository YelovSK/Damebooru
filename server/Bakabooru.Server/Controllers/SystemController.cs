using Bakabooru.Core.DTOs;
using Bakabooru.Processing.Services;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly SystemReadService _systemReadService;

    public SystemController(SystemReadService systemReadService)
    {
        _systemReadService = systemReadService;
    }

    [HttpGet("info")]
    public async Task<ActionResult<SystemInfoDto>> GetInfo(CancellationToken cancellationToken = default)
    {
        return Ok(await _systemReadService.GetInfoAsync(cancellationToken));
    }
}
