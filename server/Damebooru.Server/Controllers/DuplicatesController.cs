using Damebooru.Core.DTOs;
using Damebooru.Processing.Services.Duplicates;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DuplicatesController : ControllerBase
{
    private readonly DuplicateWriteService _duplicateWriteService;
    private readonly DuplicateReadService _duplicateReadService;

    public DuplicatesController(DuplicateWriteService duplicateWriteService, DuplicateReadService duplicateReadService)
    {
        _duplicateWriteService = duplicateWriteService;
        _duplicateReadService = duplicateReadService;
    }

    /// <summary>
    /// Returns all unresolved duplicate groups with their post details.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DuplicateGroupDto>>> GetDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateReadService.GetDuplicateGroupsAsync(cancellationToken));
    }

    /// <summary>
    /// Returns all resolved duplicate groups with their post details.
    /// </summary>
    [HttpGet("resolved")]
    public async Task<ActionResult<IEnumerable<DuplicateGroupDto>>> GetResolvedDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateReadService.GetResolvedDuplicateGroupsAsync(cancellationToken));
    }

    /// <summary>
    /// Returns unresolved duplicate candidates grouped by same library+folder partitions.
    /// </summary>
    [HttpGet("same-folder")]
    public async Task<ActionResult<IEnumerable<SameFolderDuplicateGroupDto>>> GetSameFolderDuplicateGroups(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateReadService.GetSameFolderDuplicateGroupsAsync(cancellationToken));
    }

    /// <summary>
    /// Resolve a group by keeping all posts (dismiss the group).
    /// </summary>
    [HttpPost("{groupId}/keep-all")]
    public async Task<IActionResult> KeepAll(int groupId)
    {
        return await _duplicateWriteService.KeepAllAsync(groupId).ToHttpResult();
    }

    /// <summary>
    /// Mark a resolved group as unresolved so it appears in active duplicate review again.
    /// </summary>
    [HttpPost("{groupId}/mark-unresolved")]
    public async Task<IActionResult> MarkUnresolved(int groupId, CancellationToken cancellationToken)
    {
        return await _duplicateWriteService.MarkUnresolvedAsync(groupId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Mark all eligible resolved groups as unresolved.
    /// </summary>
    [HttpPost("resolved/mark-all-unresolved")]
    public async Task<ActionResult<MarkAllUnresolvedResponseDto>> MarkAllUnresolved(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateWriteService.MarkAllUnresolvedAsync(cancellationToken));
    }

    /// <summary>
    /// Auto-resolve one duplicate group by keeping the highest-quality post.
    /// Other posts are removed from booru and excluded from future imports.
    /// </summary>
    [HttpPost("{groupId}/auto-resolve")]
    public async Task<IActionResult> AutoResolveGroup(int groupId, CancellationToken cancellationToken)
    {
        return await _duplicateWriteService.AutoResolveGroupAsync(groupId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Explicitly exclude one post from a duplicate group.
    /// Removed posts are added to the exclusion list so they won't be re-imported.
    /// Files on disk are NOT deleted.
    /// </summary>
    [HttpPost("{groupId}/exclude/{postId}")]
    public async Task<IActionResult> ExcludePost(int groupId, int postId, CancellationToken cancellationToken)
    {
        return await _duplicateWriteService.ExcludeDuplicatePostAsync(groupId, postId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Explicitly delete one post from a duplicate group.
    /// Removed posts are deleted from the booru AND the file is deleted from disk.
    /// Only allowed when the group contains at least one duplicate in the same folder as the target post.
    /// </summary>
    [HttpPost("{groupId}/delete/{postId}")]
    public async Task<IActionResult> DeletePost(int groupId, int postId, CancellationToken cancellationToken)
    {
        return await _duplicateWriteService.DeleteDuplicatePostAsync(groupId, postId, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Bulk-resolve all unresolved duplicate groups by keeping the highest-quality post in each.
    /// </summary>
    [HttpPost("resolve-all")]
    public async Task<ActionResult<ResolveAllExactResponseDto>> ResolveAll(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateWriteService.ResolveAllAsync(cancellationToken));
    }

    /// <summary>
    /// Bulk-resolve all exact (content-hash) duplicate groups by keeping the oldest post in each.
    /// </summary>
    [HttpPost("resolve-all-exact")]
    public async Task<ActionResult<ResolveAllExactResponseDto>> ResolveAllExact()
    {
        return Ok(await _duplicateWriteService.ResolveAllExactAsync());
    }


    /// <summary>
    /// Resolve one same-folder duplicate partition by keeping the best quality post.
    /// </summary>
    [HttpPost("same-folder/resolve-group")]
    public async Task<IActionResult> ResolveSameFolderGroup(
        [FromBody] ResolveSameFolderGroupRequestDto request,
        CancellationToken cancellationToken)
    {
        return await _duplicateWriteService.ResolveSameFolderGroupAsync(request, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Resolve all same-folder duplicate partitions by keeping the best quality post in each.
    /// </summary>
    [HttpPost("same-folder/resolve-all")]
    public async Task<IActionResult> ResolveAllSameFolder([FromQuery] bool exactOnly = false, CancellationToken cancellationToken = default)
    {
        return await _duplicateWriteService.ResolveAllSameFolderAsync(exactOnly, cancellationToken).ToHttpResult();
    }

    /// <summary>
    /// Returns all excluded files (e.g. from duplicate resolution).
    /// </summary>
    [HttpGet("excluded")]
    public async Task<ActionResult<IEnumerable<ExcludedFileDto>>> GetExcludedFiles(CancellationToken cancellationToken)
    {
        return Ok(await _duplicateReadService.GetExcludedFilesAsync(cancellationToken));
    }

    /// <summary>
    /// Remove a file from the exclusion list. It will be re-imported on the next scan.
    /// </summary>
    [HttpDelete("excluded/{id}")]
    public async Task<IActionResult> UnexcludeFile(int id)
    {
        return await _duplicateWriteService.UnexcludeFileAsync(id).ToHttpResult();
    }

    /// <summary>
    /// Remove all files from the exclusion list.
    /// </summary>
    [HttpDelete("excluded")]
    public async Task<ActionResult<ClearExcludedFilesResponseDto>> UnexcludeAllFiles(CancellationToken cancellationToken)
    {
        var removed = await _duplicateWriteService.UnexcludeAllFilesAsync(cancellationToken);
        return Ok(new ClearExcludedFilesResponseDto { Removed = removed });
    }

    /// <summary>
    /// Serves the original file content for an excluded file.
    /// </summary>
    [HttpGet("excluded/{id}/content")]
    public async Task<IActionResult> GetExcludedFileContent(int id, CancellationToken cancellationToken)
    {
        return await _duplicateReadService.GetExcludedFileContentPathAsync(id, cancellationToken)
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
