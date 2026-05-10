using Damebooru.Core.DTOs;
using Damebooru.Processing.Services.AutoTagging;
using Damebooru.Processing.Services.Duplicates;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SettingsController : ControllerBase
{
    private readonly AutoTagDiscoverySettingsService _autoTagDiscoverySettingsService;
    private readonly DuplicateDetectionSettingsService _duplicateDetectionSettingsService;

    public SettingsController(
        AutoTagDiscoverySettingsService autoTagDiscoverySettingsService,
        DuplicateDetectionSettingsService duplicateDetectionSettingsService)
    {
        _autoTagDiscoverySettingsService = autoTagDiscoverySettingsService;
        _duplicateDetectionSettingsService = duplicateDetectionSettingsService;
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

    [HttpGet("duplicates")]
    public async Task<ActionResult<DuplicateDetectionSettingsDto>> GetDuplicates(CancellationToken cancellationToken = default)
    {
        return Ok(await _duplicateDetectionSettingsService.GetAsync(cancellationToken));
    }

    [HttpPut("duplicates")]
    public async Task<IActionResult> UpdateDuplicates(
        [FromBody] DuplicateDetectionSettingsDto dto,
        CancellationToken cancellationToken = default)
    {
        return await _duplicateDetectionSettingsService.UpdateAsync(dto, cancellationToken).ToHttpResult();
    }
}
