using Bakabooru.Core.DTOs;
using Bakabooru.Processing.Services;
using Bakabooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PostsController : ControllerBase
{
    private readonly PostReadService _postReadService;
    private readonly PostWriteService _postWriteService;
    private readonly PostContentService _postContentService;

    public PostsController(PostReadService postReadService, PostWriteService postWriteService, PostContentService postContentService)
    {
        _postReadService = postReadService;
        _postWriteService = postWriteService;
        _postContentService = postContentService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPosts(
        [FromQuery] string? tags = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        return await _postReadService.GetPostsAsync(tags, page, pageSize, cancellationToken).ToHttpResult();
    }

    [HttpGet("{id}/around")]
    public async Task<IActionResult> GetPostsAround(int id, [FromQuery] string? tags = null, CancellationToken cancellationToken = default)
    {
        return await _postReadService.GetPostsAroundAsync(id, tags, cancellationToken).ToHttpResult();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPost(int id, CancellationToken cancellationToken = default)
    {
        return await _postReadService.GetPostAsync(id, cancellationToken).ToHttpResult();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePostMetadata(int id, [FromBody] UpdatePostMetadataDto dto)
    {
        return await _postWriteService.UpdateMetadataAsync(id, dto).ToHttpResult();
    }

    [HttpPost("{id}/tags")]
    public async Task<IActionResult> AddTag(int id, [FromBody] string tagName)
    {
        return await _postWriteService.AddTagAsync(id, tagName).ToHttpResult();
    }

    [HttpDelete("{id}/tags/{tagName}")]
    public async Task<IActionResult> RemoveTag(int id, string tagName)
    {
        return await _postWriteService.RemoveTagAsync(id, tagName).ToHttpResult();
    }

    [HttpPost("{id}/favorite")]
    public async Task<IActionResult> Favorite(int id)
    {
        return await _postWriteService.FavoriteAsync(id).ToHttpResult();
    }

    [HttpDelete("{id}/favorite")]
    public async Task<IActionResult> Unfavorite(int id)
    {
        return await _postWriteService.UnfavoriteAsync(id).ToHttpResult();
    }

    [HttpGet("{id}/sources")]
    public async Task<IActionResult> GetSources(int id, CancellationToken cancellationToken = default)
    {
        return await _postReadService.GetPostAsync(id, cancellationToken).ToHttpResult(post => Ok(post?.Sources ?? []));
    }

    [HttpPut("{id}/sources")]
    public async Task<IActionResult> SetSources(int id, [FromBody] List<string> sources)
    {
        return await _postWriteService.SetSourcesAsync(id, sources ?? []).ToHttpResult();
    }

    [HttpGet("{id}/content")]
    public async Task<IActionResult> GetPostContent(int id, CancellationToken cancellationToken = default)
    {
        return await _postContentService
            .GetPostContentAsync(id, cancellationToken)
            .ToHttpResult(descriptor => PhysicalFile(descriptor!.FullPath, descriptor.ContentType, enableRangeProcessing: true));
    }
}
