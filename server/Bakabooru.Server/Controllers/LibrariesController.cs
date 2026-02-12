using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
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
    private readonly IJobService _jobService;
    private readonly IScannerService _scannerService;

    public LibrariesController(BakabooruDbContext context, IJobService jobService, IScannerService scannerService)
    {
        _context = context;
        _jobService = jobService;
        _scannerService = scannerService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LibraryDto>>> GetLibraries()
    {
        var libraries = await _context.Libraries
            .AsNoTracking()
            .ToListAsync();

        var stats = await _context.Posts
            .AsNoTracking()
            .GroupBy(p => p.LibraryId)
            .Select(g => new
            {
                LibraryId = g.Key,
                PostCount = g.Count(),
                TotalSizeBytes = g.Sum(p => p.SizeBytes),
                LastImportDate = g.Max(p => p.ImportDate)
            })
            .ToDictionaryAsync(x => x.LibraryId);

        return libraries.Select(l =>
        {
            stats.TryGetValue(l.Id, out var s);
            return new LibraryDto
            {
                Id = l.Id,
                Name = l.Name,
                Path = l.Path,
                ScanIntervalHours = l.ScanInterval.TotalHours,
                PostCount = s?.PostCount ?? 0,
                TotalSizeBytes = s?.TotalSizeBytes ?? 0,
                LastImportDate = s?.LastImportDate
            };
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
            Name = library.Name,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours,
            PostCount = await _context.Posts.CountAsync(p => p.LibraryId == library.Id),
            TotalSizeBytes = await _context.Posts
                .Where(p => p.LibraryId == library.Id)
                .Select(p => (long?)p.SizeBytes)
                .SumAsync() ?? 0,
            LastImportDate = await _context.Posts.Where(p => p.LibraryId == library.Id).MaxAsync(p => (DateTime?)p.ImportDate)
        };
    }

    [HttpPost]
    public async Task<ActionResult<LibraryDto>> CreateLibrary(CreateLibraryDto dto)
    {
        if (!Directory.Exists(dto.Path))
        {
            return BadRequest($"Directory specified does not exist: {dto.Path}");
        }

        var library = new Library
        {
            Name = string.IsNullOrWhiteSpace(dto.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(dto.Path))
                : dto.Name.Trim(),
            Path = dto.Path,
            ScanInterval = TimeSpan.FromHours(1) // Default
        };

        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetLibrary), new { id = library.Id }, new LibraryDto
        {
            Id = library.Id,
            Name = library.Name,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours,
            PostCount = 0,
            TotalSizeBytes = 0,
            LastImportDate = null
        });
    }

    [HttpPost("{id}/scan")]
    public async Task<ActionResult<object>> ScanLibrary(int id, CancellationToken cancellationToken)
    {
        var libraryExists = await _context.Libraries.AnyAsync(l => l.Id == id, cancellationToken);
        if (!libraryExists)
        {
            return NotFound("Library not found.");
        }

        var jobName = $"Scan Library #{id}";
        var jobId = await _jobService.StartJobAsync(
            jobName,
            ct => _scannerService.ScanLibraryAsync(id, cancellationToken: ct));

        return Accepted(new { JobId = jobId });
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

    [HttpPatch("{id}/name")]
    public async Task<ActionResult<LibraryDto>> RenameLibrary(int id, [FromBody] RenameLibraryDto dto)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        library.Name = dto.Name.Trim();
        await _context.SaveChangesAsync();

        return Ok(new LibraryDto
        {
            Id = library.Id,
            Name = library.Name,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours,
            PostCount = await _context.Posts.CountAsync(p => p.LibraryId == library.Id),
            TotalSizeBytes = await _context.Posts
                .Where(p => p.LibraryId == library.Id)
                .Select(p => (long?)p.SizeBytes)
                .SumAsync() ?? 0,
            LastImportDate = await _context.Posts.Where(p => p.LibraryId == library.Id).MaxAsync(p => (DateTime?)p.ImportDate)
        });
    }
}
