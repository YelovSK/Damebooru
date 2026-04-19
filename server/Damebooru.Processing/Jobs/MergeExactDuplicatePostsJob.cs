using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public class MergeExactDuplicatePostsJob : IJob
{
    private sealed record MergeCandidate(int Id, DateTime ImportDate, string ContentHash);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MergeExactDuplicatePostsJob> _logger;

    public static readonly JobKey JobKey = JobKeys.MergeExactDuplicatePosts;
    public const string JobName = "Merge Exact Duplicate Posts";

    public MergeExactDuplicatePostsJob(IServiceScopeFactory scopeFactory, ILogger<MergeExactDuplicatePostsJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 999;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Merges old existing exact-hash sibling posts into one survivor post while preserving tags, sources, and file paths. New exact duplicates are now attached automatically during library scan, so this is mainly a one-time cleanup/repair job for older data.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        context.Reporter.Update(new JobState
        {
            ActivityText = "Loading exact-hash duplicate groups...",
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
        });

        var candidates = await db.Posts
            .AsNoTracking()
            .Select(p => new MergeCandidate(
                p.Id,
                p.ImportDate,
                p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.ContentHash).FirstOrDefault() ?? string.Empty))
            .ToListAsync(context.CancellationToken);

        var groups = candidates
            .Where(p => p.ContentHash != string.Empty)
            .GroupBy(p => p.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ContentHash = group.Key,
                Items = group
                    .OrderBy(p => p.ImportDate)
                    .ThenBy(p => p.Id)
                    .ToList()
            })
            .Where(group => group.Items.Count > 1)
            .ToList();

        var mergedGroupCount = 0;
        var mergedPostCount = 0;

        context.Reporter.Update(new JobState
        {
            ActivityText = $"Merging exact-hash groups... (0/{groups.Count})",
            ProgressCurrent = 0,
            ProgressTotal = groups.Count,
        });

        foreach (var group in groups)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var survivorId = group.Items[0].Id;
            var loserIds = group.Items.Skip(1).Select(item => item.Id).ToList();
            if (loserIds.Count == 0)
            {
                continue;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(context.CancellationToken);

            var posts = await db.Posts
                .Where(p => p.Id == survivorId || loserIds.Contains(p.Id))
                .ToListAsync(context.CancellationToken);

            var survivor = posts.FirstOrDefault(p => p.Id == survivorId);
            if (survivor == null)
            {
                await transaction.RollbackAsync(context.CancellationToken);
                continue;
            }

            var losers = posts
                .Where(p => loserIds.Contains(p.Id))
                .OrderBy(p => p.ImportDate)
                .ThenBy(p => p.Id)
                .ToList();

            if (losers.Count == 0)
            {
                await transaction.RollbackAsync(context.CancellationToken);
                continue;
            }

            var sourceRows = await db.PostSources
                .AsNoTracking()
                .Where(ps => ps.PostId == survivorId || loserIds.Contains(ps.PostId))
                .OrderBy(ps => ps.PostId == survivorId ? 0 : 1)
                .ThenBy(ps => ps.PostId)
                .ThenBy(ps => ps.Order)
                .ToListAsync(context.CancellationToken);

            var existingSourceUrls = sourceRows
                .Where(ps => ps.PostId == survivorId)
                .Select(ps => ps.Url)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextSourceOrder = sourceRows
                .Where(ps => ps.PostId == survivorId)
                .Select(ps => ps.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1;

            foreach (var source in sourceRows.Where(ps => loserIds.Contains(ps.PostId)))
            {
                if (existingSourceUrls.Add(source.Url))
                {
                    db.PostSources.Add(new PostSource
                    {
                        PostId = survivorId,
                        Url = source.Url,
                        Order = nextSourceOrder++,
                    });
                }
            }

            var existingTagKeys = await db.PostTags
                .Where(pt => pt.PostId == survivorId)
                .Select(pt => new { pt.TagId, pt.Source })
                .ToListAsync(context.CancellationToken);
            var tagKeySet = existingTagKeys
                .Select(x => $"{x.TagId}:{(int)x.Source}")
                .ToHashSet(StringComparer.Ordinal);

            var loserTags = await db.PostTags
                .Where(pt => loserIds.Contains(pt.PostId))
                .Select(pt => new { pt.TagId, pt.Source })
                .ToListAsync(context.CancellationToken);

            foreach (var loserTag in loserTags)
            {
                var key = $"{loserTag.TagId}:{(int)loserTag.Source}";
                if (tagKeySet.Add(key))
                {
                    db.PostTags.Add(new PostTag
                    {
                        PostId = survivorId,
                        TagId = loserTag.TagId,
                        Source = loserTag.Source,
                    });
                }
            }

            var scans = await db.PostAutoTagScans
                .Where(scan => scan.PostId == survivorId || loserIds.Contains(scan.PostId))
                .OrderByDescending(scan => scan.PostId == survivorId)
                .ThenByDescending(scan => scan.LastCompletedAtUtc)
                .ThenByDescending(scan => scan.LastStartedAtUtc)
                .ThenBy(scan => scan.Id)
                .ToListAsync(context.CancellationToken);

            if (scans.Count > 0)
            {
                var scanToKeep = scans[0];
                scanToKeep.PostId = survivorId;

                if (scans.Count > 1)
                {
                    db.PostAutoTagScans.RemoveRange(scans.Skip(1));
                }
            }

            await db.PostFiles
                .Where(pf => loserIds.Contains(pf.PostId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(pf => pf.PostId, survivorId), context.CancellationToken);

            await db.PostAuditEntries
                .Where(entry => loserIds.Contains(entry.PostId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entry => entry.PostId, survivorId), context.CancellationToken);

            await db.DuplicateGroupEntries
                .Where(entry => loserIds.Contains(entry.PostId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entry => entry.PostId, survivorId), context.CancellationToken);

            survivor.IsFavorite = survivor.IsFavorite || losers.Any(post => post.IsFavorite);

            await db.PostSources
                .Where(ps => loserIds.Contains(ps.PostId))
                .ExecuteDeleteAsync(context.CancellationToken);

            await db.PostTags
                .Where(pt => loserIds.Contains(pt.PostId))
                .ExecuteDeleteAsync(context.CancellationToken);

            db.Posts.RemoveRange(losers);
            await db.SaveChangesAsync(context.CancellationToken);

            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM DuplicateGroupEntries WHERE rowid NOT IN (SELECT MIN(rowid) FROM DuplicateGroupEntries GROUP BY DuplicateGroupId, PostId);",
                context.CancellationToken);

            var undersizedGroups = await db.DuplicateGroups
                .Include(g => g.Entries)
                .Where(g => g.Entries.Count < 2)
                .ToListAsync(context.CancellationToken);
            if (undersizedGroups.Count > 0)
            {
                db.DuplicateGroups.RemoveRange(undersizedGroups);
                await db.SaveChangesAsync(context.CancellationToken);
            }

            await transaction.CommitAsync(context.CancellationToken);

            mergedGroupCount++;
            mergedPostCount += losers.Count;

            _logger.LogInformation(
                "Merged {LoserCount} exact duplicate posts into survivor {SurvivorId} for hash {ContentHash}",
                losers.Count,
                survivorId,
                group.ContentHash);

            context.Reporter.Update(new JobState
            {
                ActivityText = $"Merging exact-hash groups... ({mergedGroupCount}/{groups.Count})",
                ProgressCurrent = mergedGroupCount,
                ProgressTotal = groups.Count,
            });
        }

        var unresolvedExactGroups = await db.DuplicateGroups
            .Where(g => !g.IsResolved && g.Type == DuplicateType.Exact)
            .ToListAsync(context.CancellationToken);
        if (unresolvedExactGroups.Count > 0)
        {
            db.DuplicateGroups.RemoveRange(unresolvedExactGroups);
            await db.SaveChangesAsync(context.CancellationToken);
        }

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = mergedGroupCount,
            ProgressTotal = groups.Count,
            FinalText = $"Merged {mergedPostCount} posts across {mergedGroupCount} exact-hash groups."
        });
    }
}
