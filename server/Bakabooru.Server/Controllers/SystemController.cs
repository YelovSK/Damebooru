using Bakabooru.Core.DTOs;
using Bakabooru.Data;
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
    public async Task<ActionResult<SystemInfoDto>> GetInfo(CancellationToken cancellationToken = default)
    {
        var postCount = await _dbContext.Posts.CountAsync(cancellationToken);
        var totalSizeBytes = await _dbContext.Posts.SumAsync(p => p.SizeBytes, cancellationToken);
        var tagCount = await _dbContext.Tags.CountAsync(cancellationToken);
        var libraryCount = await _dbContext.Libraries.CountAsync(cancellationToken);

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
