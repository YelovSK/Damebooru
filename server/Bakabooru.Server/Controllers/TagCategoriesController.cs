using Bakabooru.Core.DTOs;
using Bakabooru.Processing.Services;
using Bakabooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TagCategoriesController : ControllerBase
{
    private readonly TagCategoryService _tagCategoryService;

    public TagCategoriesController(TagCategoryService tagCategoryService)
    {
        _tagCategoryService = tagCategoryService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TagCategoryDto>>> GetCategories(CancellationToken cancellationToken = default)
    {
        return Ok(await _tagCategoryService.GetCategoriesAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<TagCategoryDto>> CreateCategory(CreateTagCategoryDto dto)
    {
        var category = await _tagCategoryService.CreateCategoryAsync(dto);

        return CreatedAtAction(nameof(GetCategories), new { id = category.Id }, category);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCategory(int id, [FromBody] UpdateTagCategoryDto dto)
    {
        return await _tagCategoryService.UpdateCategoryAsync(id, dto).ToHttpResult();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        return await _tagCategoryService.DeleteCategoryAsync(id).ToHttpResult();
    }
}
