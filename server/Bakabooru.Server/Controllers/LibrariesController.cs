using Bakabooru.Core.Entities;
using Bakabooru.Data;
using Bakabooru.Server.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly BakabooruDbContext _context;

    public LibrariesController(BakabooruDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LibraryDto>>> GetLibraries()
    {
        var libraries = await _context.Libraries.ToListAsync();
        return libraries.Select(l => new LibraryDto
        {
            Id = l.Id,
            Path = l.Path,
            ScanIntervalHours = l.ScanInterval.TotalHours
        }).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<LibraryDto>> GetLibrary(int id)
    {
        var library = await _context.Libraries.FindAsync(id);

        if (library == null)
        {
            return NotFound();
        }

        return new LibraryDto
        {
            Id = library.Id,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours
        };
    }

    [HttpPost]
    public async Task<ActionResult<LibraryDto>> CreateLibrary(CreateLibraryDto dto)
    {
        if (!Directory.Exists(dto.Path))
        {
            return BadRequest($"Directory specificed does not exist: {dto.Path}");
        }

        var library = new Library
        {
            Path = dto.Path,
            ScanInterval = TimeSpan.FromHours(1) // Default
        };

        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetLibrary), new { id = library.Id }, new LibraryDto
        {
            Id = library.Id,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteLibrary(int id)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        _context.Libraries.Remove(library);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
