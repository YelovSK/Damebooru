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
    private readonly DuplicateQueryService _duplicateQueryService;

    public DuplicatesController(DuplicateService duplicateService, DuplicateQueryService duplicateQueryService)
    {
        _duplicateService = duplicateService;
        _duplicateQueryService = duplicateQueryService;
    }

    /// <summary>
    /// Returns all unresolved duplicate groups with their post details.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DuplicateGroupDto>>> GetDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateQueryService.GetDuplicateGroupsAsync(cancellationToken));
    }

    /// <summary>
    /// Returns all resolved duplicate groups with their post details.
    /// </summary>
    [HttpGet("resolved")]
    public async Task<ActionResult<IEnumerable<DuplicateGroupDto>>> GetResolvedDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateQueryService.GetResolvedDuplicateGroupsAsync(cancellationToken));
    }

    /// <summary>
    /// Returns unresolved duplicate candidates grouped by same library+folder partitions.
    /// </summary>
    [HttpGet("same-folder")]
    public async Task<ActionResult<IEnumerable<SameFolderDuplicateGroupDto>>> GetSameFolderDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateQueryService.GetSameFolderDuplicateGroupsAsync(cancellationToken));
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
    /// Mark a resolved group as unresolved so it appears in active duplicate review again.
    /// </summary>
    [HttpPost("{groupId}/mark-unresolved")]
    public async Task<IActionResult> MarkUnresolved(int groupId, CancellationToken cancellationToken)
    {
        return await _duplicateService.MarkUnresolvedAsync(groupId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Mark all eligible resolved groups as unresolved.
    /// </summary>
    [HttpPost("resolved/mark-all-unresolved")]
    public async Task<ActionResult<MarkAllUnresolvedResponseDto>> MarkAllUnresolved(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateService.MarkAllUnresolvedAsync(cancellationToken));
    }

    /// <summary>
    /// Auto-resolve one duplicate group by keeping the highest-quality post.
    /// Other posts are removed from booru and excluded from future imports.
    /// </summary>
    [HttpPost("{groupId}/auto-resolve")]
    public async Task<IActionResult> AutoResolveGroup(int groupId, CancellationToken cancellationToken)
    {
        return await _duplicateService.AutoResolveGroupAsync(groupId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Explicitly exclude one post from a duplicate group.
    /// Removed posts are added to the exclusion list so they won't be re-imported.
    /// Files on disk are NOT deleted.
    /// </summary>
    [HttpPost("{groupId}/exclude/{postId}")]
    public async Task<IActionResult> ExcludePost(int groupId, int postId, CancellationToken cancellationToken)
    {
        return await _duplicateService.ExcludeDuplicatePostAsync(groupId, postId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Explicitly delete one post from a duplicate group.
    /// Removed posts are deleted from the booru AND the file is deleted from disk.
    /// Only allowed when the group contains at least one duplicate in the same folder as the target post.
    /// </summary>
    [HttpPost("{groupId}/delete/{postId}")]
    public async Task<IActionResult> DeletePost(int groupId, int postId, CancellationToken cancellationToken)
    {
        return await _duplicateService.DeleteDuplicatePostAsync(groupId, postId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Bulk-resolve all unresolved duplicate groups by keeping the highest-quality post in each.
    /// </summary>
    [HttpPost("resolve-all")]
    public async Task<ActionResult<ResolveAllExactResponseDto>> ResolveAll(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateService.ResolveAllAsync(cancellationToken));
    }

    /// <summary>
    /// Bulk-resolve all exact (content-hash) duplicate groups by keeping the oldest post in each.
    /// </summary>
    [HttpPost("resolve-all-exact")]
    public async Task<ActionResult<ResolveAllExactResponseDto>> ResolveAllExact()
    {
        return Ok(await _duplicateService.ResolveAllExactAsync());
    }


    /// <summary>
    /// Resolve one same-folder duplicate partition by keeping the best quality post.
    /// </summary>
    [HttpPost("same-folder/resolve-group")]
    public async Task<IActionResult> ResolveSameFolderGroup(
        [FromBody] ResolveSameFolderGroupRequestDto request,
        CancellationToken cancellationToken)
    {
        return await _duplicateService.ResolveSameFolderGroupAsync(request, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Resolve all same-folder duplicate partitions by keeping the best quality post in each.
    /// </summary>
    [HttpPost("same-folder/resolve-all")]
    public async Task<IActionResult> ResolveAllSameFolder(CancellationToken cancellationToken)
    {
        return await _duplicateService.ResolveAllSameFolderAsync(cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Returns all excluded files (e.g. from duplicate resolution).
    /// </summary>
    [HttpGet("excluded")]
    public async Task<ActionResult<IEnumerable<ExcludedFileDto>>> GetExcludedFiles(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateQueryService.GetExcludedFilesAsync(cancellationToken));
    }

    /// <summary>
    /// Remove a file from the exclusion list. It will be re-imported on the next scan.
    /// </summary>
    [HttpDelete("excluded/{id}")]
    public async Task<IActionResult> UnexcludeFile(int id)
    {
        return await _duplicateService.UnexcludeFileAsync(id).ToHttpResult();
    }

    /// <summary>
    /// Serves the original file content for an excluded file.
    /// </summary>
    [HttpGet("excluded/{id}/content")]
    public async Task<IActionResult> GetExcludedFileContent(int id, CancellationToken cancellationToken)
    {
        return await _duplicateQueryService.GetExcludedFileContentPathAsync(id, cancellationToken)
            .ToHttpResult(fullPath =>
            {
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(fullPath!, out var contentType))
                {
                    contentType = "application/octet-stream";
                }
                return PhysicalFile(fullPath!, contentType, enableRangeProcessing: true);
            });
    }
}
