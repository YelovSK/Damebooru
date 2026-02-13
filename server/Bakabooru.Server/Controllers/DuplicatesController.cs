using Bakabooru.Core.DTOs;
using Bakabooru.Processing.Services;
using Bakabooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DuplicatesController : ControllerBase
{
    private readonly DuplicateService _duplicateService;

    public DuplicatesController(DuplicateService duplicateService)
    {
        _duplicateService = duplicateService;
    }

    /// <summary>
    /// Returns all unresolved duplicate groups with their post details.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DuplicateGroupDto>>> GetDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateService.GetDuplicateGroupsAsync(cancellationToken));
    }

    /// <summary>
    /// Resolve a group by keeping all posts (dismiss the group).
    /// </summary>
    [HttpPost("{groupId}/keep-all")]
    public async Task<IActionResult> KeepAll(int groupId)
    {
        return await _duplicateService.KeepAllAsync(groupId).ToHttpResult();
    }

    /// <summary>
    /// Resolve a group by keeping one post and removing the others from the booru.
    /// Removed posts are added to the exclusion list so they won't be re-imported.
    /// Files on disk are NOT deleted.
    /// </summary>
    [HttpPost("{groupId}/keep/{postId}")]
    public async Task<IActionResult> KeepOne(int groupId, int postId)
    {
        return await _duplicateService.KeepOneAsync(groupId, postId).ToHttpResult();
    }

    /// <summary>
    /// Bulk-resolve all exact (content-hash) duplicate groups by keeping the oldest post in each.
    /// </summary>
    [HttpPost("resolve-all-exact")]
    public async Task<ActionResult<ResolveAllExactResponseDto>> ResolveAllExact()
    {
        return Ok(await _duplicateService.ResolveAllExactAsync());
    }
}
