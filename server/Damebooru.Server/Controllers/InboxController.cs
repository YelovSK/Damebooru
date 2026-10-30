using Damebooru.Core.DTOs;
using Damebooru.Processing.Services;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InboxController : ControllerBase
{
    private readonly InboxSortService _inboxSortService;
    private readonly FolderSuggestionService _folderSuggestionService;

    public InboxController(InboxSortService inboxSortService, FolderSuggestionService folderSuggestionService)
    {
        _inboxSortService = inboxSortService;
        _folderSuggestionService = folderSuggestionService;
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<PostFolderSuggestionsDto>>> GetSuggestions([FromQuery] List<int> postIds, CancellationToken cancellationToken = default)
    {
        return Ok(await _folderSuggestionService.SuggestAsync(postIds, cancellationToken));
    }

    [HttpGet("suggestions/evaluation")]
    public async Task<ActionResult<FolderSuggestionEvaluationDto>> EvaluateSuggestions([FromQuery] int sampleSize = 200, CancellationToken cancellationToken = default)
    {
        return Ok(await _folderSuggestionService.EvaluateAsync(Math.Clamp(sampleSize, 1, 2000), cancellationToken));
    }

    [HttpPost("sort")]
    public async Task<IActionResult> Sort([FromBody] SortInboxFilesDto dto, CancellationToken cancellationToken = default)
    {
        return await _inboxSortService.SortAsync(dto, cancellationToken).ToHttpResult();
    }
}
