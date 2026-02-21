using Bakabooru.Core.DTOs;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class SystemReadService
{
    private readonly BakabooruDbContext _dbContext;

    public SystemReadService(BakabooruDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SystemInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var postCount = await _dbContext.Posts.CountAsync(cancellationToken);
        var totalSizeBytes = await _dbContext.Posts.SumAsync(p => p.SizeBytes, cancellationToken);
        var tagCount = await _dbContext.Tags.CountAsync(cancellationToken);
        var libraryCount = await _dbContext.Libraries.CountAsync(cancellationToken);

        return new SystemInfoDto
        {
            PostCount = postCount,
            TotalSizeBytes = totalSizeBytes,
            TagCount = tagCount,
            LibraryCount = libraryCount,
            ServerTime = DateTime.UtcNow
        };
    }
}
