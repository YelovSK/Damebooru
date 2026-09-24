using Damebooru.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

internal static class PostEnrichmentTargets
{
    public static async Task<List<PostEnrichmentTarget>> LoadAsync(IQueryable<Post> posts, CancellationToken cancellationToken)
    {
        var rows = await posts
            .Select(p => new
            {
                p.Id,
                p.ContentHash,
                File = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => new { LibraryPath = pf.Library.Path, pf.RelativePath })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => row.File != null)
            .Select(row => new PostEnrichmentTarget(row.Id, row.ContentHash, Path.Combine(row.File!.LibraryPath, row.File.RelativePath)))
            .ToList();
    }
}
