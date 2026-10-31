using Damebooru.Core.DTOs;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.AiTagging;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InboxController : ControllerBase
{
    private readonly InboxSortService _inboxSortService;
    private readonly FolderSuggestionService _folderSuggestionService;
    private readonly AiTaggingService _aiTaggingService;

    public InboxController(InboxSortService inboxSortService, FolderSuggestionService folderSuggestionService, AiTaggingService aiTaggingService)
    {
        _inboxSortService = inboxSortService;
        _folderSuggestionService = folderSuggestionService;
        _aiTaggingService = aiTaggingService;
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<PostFolderSuggestionsDto>>> GetSuggestions([FromQuery] List<int> postIds, CancellationToken cancellationToken = default)
    {
        return Ok(await _folderSuggestionService.SuggestAsync(postIds, cancellationToken));
    }

    /// <summary>Runs the AI tagger on the post, storing predictions only, and returns its new suggestions.</summary>
    [HttpPost("posts/{postId}/predict")]
    public async Task<IActionResult> Predict(int postId, CancellationToken cancellationToken = default)
    {
        var prediction = await _aiTaggingService.PredictAsync(postId, cancellationToken);
        if (!prediction.IsSuccess)
        {
            return prediction.ToHttpResult();
        }

        return Ok((await _folderSuggestionService.SuggestAsync([postId], cancellationToken)).Single());
    }

    [HttpGet("suggestions/evaluation")]
    public async Task<ActionResult<FolderSuggestionEvaluationDto>> EvaluateSuggestions([FromQuery] int sampleSize = 200, CancellationToken cancellationToken = default)
    {
        return Ok(await _folderSuggestionService.EvaluateAsync(Math.Clamp(sampleSize, 1, 2000), cancellationToken));
    }

    [HttpGet("destinations")]
    public async Task<ActionResult<List<DestinationFolderDto>>> GetDestinations(CancellationToken cancellationToken = default)
    {
        return Ok(await _inboxSortService.GetDestinationsAsync(cancellationToken));
    }

    [HttpPost("sort")]
    public async Task<IActionResult> Sort([FromBody] SortInboxPostsDto dto, CancellationToken cancellationToken = default)
    {
        return await _inboxSortService.SortAsync(dto, cancellationToken).ToHttpResult();
    }

    [HttpPost("discard")]
    public async Task<ActionResult<List<InboxPostResultDto>>> Discard([FromBody] DiscardInboxPostsDto dto, CancellationToken cancellationToken = default)
    {
        return Ok(await _inboxSortService.DiscardAsync(dto, cancellationToken));
    }
}
