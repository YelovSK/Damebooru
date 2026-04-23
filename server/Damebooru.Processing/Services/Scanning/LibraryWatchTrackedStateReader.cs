using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Damebooru.Processing.Services.Scanning;

public sealed class LibraryWatchTrackedStateReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public LibraryWatchTrackedStateReader(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> IsTrackedDirectoryPrefixAsync(int libraryId, string relativePath, CancellationToken cancellationToken)
    {
        var normalizedPrefix = RelativePathMatcher.NormalizePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
        {
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var prefixWithSlash = normalizedPrefix + "/";
        return await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => pf.LibraryId == libraryId)
            .Select(pf => pf.RelativePath.Replace("\\", "/"))
            .AnyAsync(
                path => path == normalizedPrefix || path.StartsWith(prefixWithSlash),
                cancellationToken);
    }

    public async Task<FileIdentity?> LoadTrackedIdentityAsync(int libraryId, string relativePath, CancellationToken cancellationToken)
    {
        var normalizedRelativePath = RelativePathMatcher.NormalizePath(relativePath);
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var identity = await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => pf.LibraryId == libraryId && pf.RelativePath.Replace("\\", "/") == normalizedRelativePath)
            .Select(pf => new { pf.FileIdentityDevice, pf.FileIdentityValue })
            .FirstOrDefaultAsync(cancellationToken);

        if (identity == null || string.IsNullOrWhiteSpace(identity.FileIdentityDevice) || string.IsNullOrWhiteSpace(identity.FileIdentityValue))
        {
            return null;
        }

        return new FileIdentity(identity.FileIdentityDevice, identity.FileIdentityValue);
    }
}
