using Damebooru.Core.DTOs;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

internal static class PostDtoLoader
{
    public static async Task<PostDto?> LoadPostAsync(
        DamebooruDbContext dbContext,
        int id,
        CancellationToken cancellationToken)
    {
        var postsById = await LoadPostsByIdAsync(dbContext, [id], cancellationToken);
        return postsById.GetValueOrDefault(id);
    }

    public static async Task<Dictionary<int, PostDto>> LoadPostsByIdAsync(
        DamebooruDbContext dbContext,
        IEnumerable<int> ids,
        CancellationToken cancellationToken)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var posts = await dbContext.Posts
            // The duplicate-group include graph cycles back through Post, which EF does not allow in no-tracking queries.
            .AsSplitQuery()
            .Include(p => p.PostFiles)
                .ThenInclude(pf => pf.Library)
            .Include(p => p.Sources)
            .Include(p => p.PostTags)
                .ThenInclude(pt => pt.Tag)
            .Include(p => p.DuplicateGroupEntries)
                .ThenInclude(dge => dge.DuplicateGroup)
                    .ThenInclude(g => g.Entries)
                        .ThenInclude(e => e.Post)
                            .ThenInclude(sp => sp.PostFiles)
            .Where(p => idList.Contains(p.Id))
            .ToListAsync(cancellationToken);

        return posts.ToDictionary(p => p.Id, PostDto.FromPost);
    }
}
