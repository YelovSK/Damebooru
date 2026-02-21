using Damebooru.Core.DTOs;
using Damebooru.Processing.Services;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly LibraryService _libraryService;

    public LibrariesController(LibraryService libraryService)
    {
        _libraryService = libraryService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LibraryDto>>> GetLibraries(CancellationToken cancellationToken = default)
    {
        return Ok(await _libraryService.GetLibrariesAsync(cancellationToken));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetLibrary(int id, CancellationToken cancellationToken = default)
    {
        return await _libraryService.GetLibraryAsync(id, cancellationToken).ToHttpResult();
    }

    [HttpPost]
    public async Task<IActionResult> CreateLibrary(CreateLibraryDto dto)
    {
        return await _libraryService
            .CreateLibraryAsync(dto)
            .ToHttpResult(library => CreatedAtAction(nameof(GetLibrary), new { id = library!.Id }, library));
    }

    [HttpGet("{id}/ignored-paths")]
    public async Task<IActionResult> GetIgnoredPaths(int id, CancellationToken cancellationToken = default)
    {
        return await _libraryService.GetIgnoredPathsAsync(id, cancellationToken).ToHttpResult();
    }

    [HttpPost("{id}/ignored-paths")]
    public async Task<IActionResult> AddIgnoredPath(int id, [FromBody] AddLibraryIgnoredPathDto dto)
    {
        return await _libraryService.AddIgnoredPathAsync(id, dto).ToHttpResult();
    }

    [HttpDelete("{id}/ignored-paths/{ignoredPathId:int}")]
    public async Task<IActionResult> DeleteIgnoredPath(int id, int ignoredPathId)
    {
        return await _libraryService.DeleteIgnoredPathAsync(id, ignoredPathId).ToHttpResult();
    }

    [HttpPost("{id}/scan")]
    public async Task<IActionResult> ScanLibrary(int id)
    {
        return await _libraryService
            .ScanLibraryAsync(id)
            .ToHttpResult(jobId => Accepted(new { JobId = jobId }));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteLibrary(int id)
    {
        return await _libraryService.DeleteLibraryAsync(id).ToHttpResult();
    }

    [HttpPatch("{id}/name")]
    public async Task<IActionResult> RenameLibrary(int id, [FromBody] RenameLibraryDto dto)
    {
        return await _libraryService.RenameLibraryAsync(id, dto).ToHttpResult(_ => NoContent());
    }
}
