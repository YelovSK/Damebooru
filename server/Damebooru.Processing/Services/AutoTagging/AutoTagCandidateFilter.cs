using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AutoTagging;

internal static class AutoTagCandidateFilter
{
    private sealed record CandidatePath(int PostId, int LibraryId, string RelativePath);

    public static async Task<List<int>> ExcludeAutoTagIgnoredPathsAsync(
        DamebooruDbContext db,
        List<int> candidatePostIds,
        CancellationToken cancellationToken)
    {
        if (candidatePostIds.Count == 0)
        {
            return candidatePostIds;
        }

        var excludedPrefixesByLibrary = (await db.LibraryAutoTagExcludedPaths
            .AsNoTracking()
            .Select(p => new { p.LibraryId, p.RelativePathPrefix })
            .ToListAsync(cancellationToken))
            .GroupBy(p => p.LibraryId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.RelativePathPrefix).ToList());

        if (excludedPrefixesByLibrary.Count == 0)
        {
            return candidatePostIds;
        }

        var excludedPostIds = new HashSet<int>();
        const int batchSize = 500;
        for (var i = 0; i < candidatePostIds.Count; i += batchSize)
        {
            var batchIds = candidatePostIds.Skip(i).Take(batchSize).ToList();
            var candidatePaths = await db.PostFiles
                .AsNoTracking()
                .Where(pf => batchIds.Contains(pf.PostId) && EF.Functions.Like(pf.ContentType, "image/%"))
                .Select(pf => new CandidatePath(pf.PostId, pf.LibraryId, pf.RelativePath))
                .ToListAsync(cancellationToken);

            foreach (var candidatePath in candidatePaths)
            {
                if (excludedPrefixesByLibrary.TryGetValue(candidatePath.LibraryId, out var prefixes)
                    && prefixes.Any(prefix => RelativePathMatcher.IsWithinPrefix(candidatePath.RelativePath, prefix)))
                {
                    excludedPostIds.Add(candidatePath.PostId);
                }
            }
        }

        return candidatePostIds
            .Where(id => !excludedPostIds.Contains(id))
            .ToList();
    }
}
