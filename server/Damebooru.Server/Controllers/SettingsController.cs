using Damebooru.Core.DTOs;
using Damebooru.Processing.Services.AutoTagging;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SettingsController : ControllerBase
{
    private readonly AutoTagDiscoverySettingsService _autoTagDiscoverySettingsService;

    public SettingsController(AutoTagDiscoverySettingsService autoTagDiscoverySettingsService)
    {
        _autoTagDiscoverySettingsService = autoTagDiscoverySettingsService;
    }

    [HttpGet("auto-tagging")]
    public async Task<ActionResult<AutoTagDiscoverySettingsDto>> GetAutoTagging(CancellationToken cancellationToken = default)
    {
        return Ok(await _autoTagDiscoverySettingsService.GetAsync(cancellationToken));
    }

    [HttpPut("auto-tagging")]
    public async Task<IActionResult> UpdateAutoTagging([FromBody] AutoTagDiscoverySettingsDto dto, CancellationToken cancellationToken = default)
    {
        return await _autoTagDiscoverySettingsService.UpdateAsync(dto, cancellationToken).ToHttpResult();
    }
}
