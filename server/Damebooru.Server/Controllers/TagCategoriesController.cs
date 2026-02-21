using Damebooru.Core.DTOs;
using Damebooru.Processing.Services;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

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
    public async Task<IActionResult> CreateCategory([FromBody] CreateTagCategoryDto dto)
    {
        return await _tagCategoryService.CreateCategoryAsync(dto)
            .ToHttpResult(created => Created($"/api/tagcategories/{created!.Id}", created));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCategory(int id, [FromBody] UpdateTagCategoryDto dto)
    {
        return await _tagCategoryService.UpdateCategoryAsync(id, dto).ToHttpResult(_ => NoContent());
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        return await _tagCategoryService.DeleteCategoryAsync(id).ToHttpResult();
    }
}
