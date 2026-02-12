using Bakabooru.Data;
using Bakabooru.Server.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly BakabooruDbContext _dbContext;

    public SystemController(BakabooruDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("info")]
    public async Task<ActionResult<SystemInfoDto>> GetInfo()
    {
        var postCount = await _dbContext.Posts.CountAsync();
        var totalSizeBytes = await _dbContext.Posts.SumAsync(p => p.SizeBytes);
        var tagCount = await _dbContext.Tags.CountAsync();
        var libraryCount = await _dbContext.Libraries.CountAsync();

        return Ok(new SystemInfoDto
        {
            PostCount = postCount,
            TotalSizeBytes = totalSizeBytes,
            TagCount = tagCount,
            LibraryCount = libraryCount,
            ServerTime = DateTime.UtcNow
        });
    }
}
