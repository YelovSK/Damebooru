using Bakabooru.Core.DTOs;
using Bakabooru.Processing.Services;
using Bakabooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TagsController : ControllerBase
{
    private readonly TagService _tagService;

    public TagsController(TagService tagService)
    {
        _tagService = tagService;
    }

    [HttpGet]
    public async Task<ActionResult<TagListDto>> GetTags(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _tagService.GetTagsAsync(query, page, pageSize, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> CreateTag([FromBody] CreateTagDto dto)
    {
        return await _tagService.CreateTagAsync(dto).ToHttpResult();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTag(int id, [FromBody] UpdateTagDto dto)
    {
        return await _tagService.UpdateTagAsync(id, dto).ToHttpResult();
    }

    [HttpPost("{id}/merge")]
    public async Task<IActionResult> MergeTag(int id, [FromBody] MergeTagDto dto)
    {
        return await _tagService.MergeTagAsync(id, dto.TargetTagId).ToHttpResult();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTag(int id)
    {
        return await _tagService.DeleteTagAsync(id).ToHttpResult();
    }
}
