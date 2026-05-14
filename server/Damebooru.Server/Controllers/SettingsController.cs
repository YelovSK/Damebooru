using Damebooru.Core.DTOs;
using Damebooru.Processing.Services.AiTagging;
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
    private readonly AiTaggingSettingsService _aiTaggingSettingsService;
    private readonly DuplicateDetectionSettingsService _duplicateDetectionSettingsService;

    public SettingsController(
        AutoTagDiscoverySettingsService autoTagDiscoverySettingsService,
        AiTaggingSettingsService aiTaggingSettingsService,
        DuplicateDetectionSettingsService duplicateDetectionSettingsService)
    {
        _autoTagDiscoverySettingsService = autoTagDiscoverySettingsService;
        _aiTaggingSettingsService = aiTaggingSettingsService;
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

    [HttpGet("ai-tagging")]
    public async Task<ActionResult<AiTaggingSettingsDto>> GetAiTagging(CancellationToken cancellationToken = default)
    {
        return Ok(await _aiTaggingSettingsService.GetAsync(cancellationToken));
    }

    [HttpPut("ai-tagging")]
    public async Task<IActionResult> UpdateAiTagging([FromBody] AiTaggingSettingsDto dto, CancellationToken cancellationToken = default)
    {
        return await _aiTaggingSettingsService.UpdateAsync(dto, cancellationToken).ToHttpResult();
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
