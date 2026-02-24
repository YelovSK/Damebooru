using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Damebooru.Processing.Jobs;

public class FindDuplicatesJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.FindDuplicates;
    public const string JobName = "Find Duplicates";

    private readonly record struct HashPost(
        int Id,
        ulong W0, ulong W1, ulong W2, ulong W3,
        string ContentType);

    private sealed record DuplicatePostCandidate(int Id, string ContentHash, string? PdqHash256, string ContentType);
    private sealed record PerceptualResult(List<DuplicateGroup> Groups, int PerceptualGroupCount, int MatchedPairs, int TotalComparisons);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FindDuplicatesJob> _logger;

    public int CombinedSimilarityThresholdPercent { get; set; } = 68;

    public FindDuplicatesJob(IServiceScopeFactory scopeFactory, ILogger<FindDuplicatesJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 40;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Scans for exact (content hash) and PDQ-based perceptual duplicate posts.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        context.Reporter.Update(new JobState
        {
            ActivityText = "Loading posts...",
            ProgressCurrent = null,
            ProgressTotal = null,
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
        });
        var posts = await db.Posts
            .AsNoTracking()
            .Select(p => new DuplicatePostCandidate(p.Id, p.ContentHash, p.PdqHash256, p.ContentType))
            .ToListAsync(context.CancellationToken);
        context.Reporter.Update(new JobState
        {
            ActivityText = $"Loading posts... ({posts.Count}/{posts.Count})",
            ProgressCurrent = posts.Count,
            ProgressTotal = posts.Count
        });

        _logger.LogInformation("Loaded {Count} posts for duplicate analysis", posts.Count);

        context.Reporter.Update(new JobState
        {
            ActivityText = "Clearing old unresolved groups...",
            ProgressCurrent = null,
            ProgressTotal = null,
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
        });
        var oldGroups = await db.DuplicateGroups
            .Where(g => !g.IsResolved)
            .ToListAsync(context.CancellationToken);
        db.DuplicateGroups.RemoveRange(oldGroups);
        await db.SaveChangesAsync(context.CancellationToken);
        context.Reporter.Update(new JobState
        {
            ActivityText = $"Clearing old unresolved groups... ({oldGroups.Count}/{oldGroups.Count})",
            ProgressCurrent = oldGroups.Count,
            ProgressTotal = oldGroups.Count
        });

        context.Reporter.Update(new JobState
        {
            ActivityText = "Loading resolved groups...",
            ProgressCurrent = null,
            ProgressTotal = null,
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
        });
        var resolvedGroups = await db.DuplicateGroups
            .Where(g => g.IsResolved)
            .Include(g => g.Entries)
            .ToListAsync(context.CancellationToken);
        var resolvedGroupSignatures = resolvedGroups
            .Select(group => string.Join(",", group.Entries.Select(e => e.PostId).OrderBy(id => id)))
            .ToHashSet();

        context.Reporter.Update(new JobState
        {
            ActivityText = $"Finding exact duplicates... (0/{posts.Count})",
            ProgressCurrent = 0,
            ProgressTotal = posts.Count
        });

        var detectedAtUtc = DateTime.UtcNow;
        var (exactGroups, exactPostIds) = BuildExactGroups(posts, resolvedGroupSignatures, detectedAtUtc);

        _logger.LogInformation("Found {Count} exact duplicate groups", exactGroups.Count);
        context.Reporter.Update(new JobState
        {
            ActivityText = $"Finding exact duplicates... ({posts.Count}/{posts.Count})",
            ProgressCurrent = posts.Count,
            ProgressTotal = posts.Count
        });

        context.Reporter.Update(new JobState
        {
            ActivityText = "Finding perceptual duplicates... (0 processed)",
            ProgressCurrent = null,
            ProgressTotal = null,
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
        });

        var perceptual = BuildPerceptualGroups(
            posts,
            resolvedGroupSignatures,
            exactPostIds,
            CombinedSimilarityThresholdPercent,
            detectedAtUtc,
            (current, total) =>
            {
                context.Reporter.Update(new JobState
                {
                    ActivityText = $"Comparing PDQ hashes... ({current}/{total} comparisons)",
                    ProgressCurrent = current,
                    ProgressTotal = total
                });
            });

        _logger.LogInformation("Found {Count} perceptual duplicate groups", perceptual.PerceptualGroupCount);
        context.Reporter.Update(new JobState
        {
            ActivityText = $"Finding perceptual duplicates... ({perceptual.TotalComparisons}/{perceptual.TotalComparisons})",
            ProgressCurrent = perceptual.TotalComparisons,
            ProgressTotal = perceptual.TotalComparisons
        });

        context.Reporter.Update(new JobState
        {
            ActivityText = "Saving duplicate groups...",
            ProgressCurrent = null,
            ProgressTotal = null,
            ClearProgressCurrent = true,
            ClearProgressTotal = true,
        });

        var newGroups = new List<DuplicateGroup>(exactGroups.Count + perceptual.Groups.Count);
        newGroups.AddRange(exactGroups);
        newGroups.AddRange(perceptual.Groups);

        if (newGroups.Count > 0)
        {
            db.DuplicateGroups.AddRange(newGroups);
            await db.SaveChangesAsync(context.CancellationToken);
        }

        var totalEntries = newGroups.Sum(g => g.Entries.Count);
        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = totalEntries,
            ProgressTotal = totalEntries,
            FinalText = $"Found {newGroups.Count} duplicate groups ({totalEntries} posts)."
        });

        _logger.LogInformation("Duplicate scan complete: {Groups} groups, {Entries} total posts", newGroups.Count, totalEntries);
    }

    private static (List<DuplicateGroup> Groups, HashSet<int> ExactPostIds) BuildExactGroups(
        IReadOnlyCollection<DuplicatePostCandidate> posts,
        ISet<string> resolvedGroupSignatures,
        DateTime detectedAtUtc)
    {
        var groups = new List<DuplicateGroup>();

        var exactGroups = posts
            .Where(p => !string.IsNullOrEmpty(p.ContentHash))
            .GroupBy(p => p.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in exactGroups)
        {
            var postIds = group.Select(p => p.Id).OrderBy(id => id).ToList();
            var signature = string.Join(",", postIds);

            if (resolvedGroupSignatures.Contains(signature))
            {
                continue;
            }

            groups.Add(new DuplicateGroup
            {
                Type = DuplicateType.Exact,
                DetectedDate = detectedAtUtc,
                Entries = postIds.Select(id => new DuplicateGroupEntry { PostId = id }).ToList()
            });
        }

        var exactPostIds = groups
            .SelectMany(g => g.Entries)
            .Select(e => e.PostId)
            .ToHashSet();

        return (groups, exactPostIds);
    }

    private static PerceptualResult BuildPerceptualGroups(
        IReadOnlyCollection<DuplicatePostCandidate> posts,
        ISet<string> resolvedGroupSignatures,
        ISet<int> exactPostIds,
        int combinedSimilarityThresholdPercent,
        DateTime detectedAtUtc,
        Action<int, int>? progress)
    {
        var hashPosts = new List<HashPost>();
        foreach (var p in posts)
        {
            if (!string.IsNullOrWhiteSpace(p.PdqHash256) && TryParseHex256(p.PdqHash256, out var words))
            {
                hashPosts.Add(new HashPost(p.Id, words[0], words[1], words[2], words[3], p.ContentType));
            }
        }

        var neighbors = new Dictionary<int, HashSet<int>>();
        var pairSimilarity = new Dictionary<long, int>();

        int totalComparisons = hashPosts.Count * (hashPosts.Count - 1) / 2;
        int comparedSoFar = 0;
        int lastReportedPercent = 30;
        int matchedPairs = 0;

        for (int i = 0; i < hashPosts.Count; i++)
        {
            for (int j = i + 1; j < hashPosts.Count; j++)
            {
                comparedSoFar++;

                if (TryComputeSimilarity(hashPosts[i], hashPosts[j], combinedSimilarityThresholdPercent, out var similarity))
                {
                    var idA = hashPosts[i].Id;
                    var idB = hashPosts[j].Id;

                    if (!(exactPostIds.Contains(idA) && exactPostIds.Contains(idB)))
                    {
                        AddEdge(neighbors, idA, idB);
                        pairSimilarity[GetPairKey(idA, idB)] = similarity;
                        matchedPairs++;
                    }
                }

                if (totalComparisons > 0)
                {
                    int percent = 30 + (int)((double)comparedSoFar / totalComparisons * 60);
                    if (percent > lastReportedPercent + 2)
                    {
                        lastReportedPercent = percent;
                        progress?.Invoke(comparedSoFar, totalComparisons);
                    }
                }
            }
        }

        var groups = new List<DuplicateGroup>();
        var perceptualCount = 0;
        var remaining = new HashSet<int>(neighbors.Keys);

        while (remaining.Count > 0)
        {
            var seed = remaining
                .OrderByDescending(id => GetRemainingDegree(id, remaining, neighbors))
                .ThenBy(id => id)
                .First();

            var groupMembers = BuildCliqueGroup(seed, remaining, neighbors, pairSimilarity);
            if (groupMembers.Count < 2)
            {
                remaining.Remove(seed);
                continue;
            }

            var postIds = groupMembers.OrderBy(id => id).ToList();
            var signature = string.Join(",", postIds);
            if (resolvedGroupSignatures.Contains(signature))
            {
                foreach (var member in groupMembers)
                {
                    remaining.Remove(member);
                }
                continue;
            }

            groups.Add(new DuplicateGroup
            {
                Type = DuplicateType.Perceptual,
                SimilarityPercent = CalculateGroupSimilarity(groupMembers, pairSimilarity),
                DetectedDate = detectedAtUtc,
                Entries = postIds.Select(id => new DuplicateGroupEntry { PostId = id }).ToList()
            });
            perceptualCount++;

            foreach (var member in groupMembers)
            {
                remaining.Remove(member);
            }
        }

        return new PerceptualResult(groups, perceptualCount, matchedPairs, totalComparisons);
    }

    private static bool TryComputeSimilarity(HashPost a, HashPost b, int combinedSimilarityThresholdPercent, out int similarityPercent)
    {
        var distance = BitOperations.PopCount(a.W0 ^ b.W0)
                     + BitOperations.PopCount(a.W1 ^ b.W1)
                     + BitOperations.PopCount(a.W2 ^ b.W2)
                     + BitOperations.PopCount(a.W3 ^ b.W3);

        var similarity = 1.0 - (double)distance / 256;
        var threshold = combinedSimilarityThresholdPercent / 100.0;

        var isAImage = a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isBImage = b.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        if (!isAImage || !isBImage)
        {
            threshold = Math.Max(threshold, 0.90);
        }

        if (similarity < threshold)
        {
            similarityPercent = 0;
            return false;
        }

        similarityPercent = (int)Math.Round(similarity * 100);
        return true;
    }

    private static bool TryParseHex256(string hex, out ulong[] words)
    {
        words = [0UL, 0UL, 0UL, 0UL];

        var trimmed = hex.Trim();
        if (trimmed.Length != 64)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            var segment = trimmed.Substring(i * 16, 16);
            if (!ulong.TryParse(segment, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            {
                return false;
            }

            words[i] = parsed;
        }

        return true;
    }

    private static void AddEdge(Dictionary<int, HashSet<int>> neighbors, int a, int b)
    {
        if (!neighbors.TryGetValue(a, out var setA))
        {
            setA = [];
            neighbors[a] = setA;
        }

        if (!neighbors.TryGetValue(b, out var setB))
        {
            setB = [];
            neighbors[b] = setB;
        }

        setA.Add(b);
        setB.Add(a);
    }

    private static long GetPairKey(int a, int b)
    {
        var min = Math.Min(a, b);
        var max = Math.Max(a, b);
        return ((long)min << 32) | (uint)max;
    }

    private static int GetRemainingDegree(int id, HashSet<int> remaining, Dictionary<int, HashSet<int>> neighbors)
    {
        if (!neighbors.TryGetValue(id, out var set))
        {
            return 0;
        }

        var degree = 0;
        foreach (var neighbor in set)
        {
            if (remaining.Contains(neighbor))
            {
                degree++;
            }
        }

        return degree;
    }

    private static List<int> BuildCliqueGroup(
        int seed,
        HashSet<int> remaining,
        Dictionary<int, HashSet<int>> neighbors,
        Dictionary<long, int> pairSimilarity)
    {
        var group = new List<int> { seed };

        if (!neighbors.TryGetValue(seed, out var seedNeighbors))
        {
            return group;
        }

        var candidates = seedNeighbors
            .Where(remaining.Contains)
            .OrderByDescending(id => pairSimilarity.GetValueOrDefault(GetPairKey(seed, id), 0))
            .ThenBy(id => id)
            .ToList();

        foreach (var candidate in candidates)
        {
            var connectedToAll = group
                .Where(member => member != candidate)
                .All(member => pairSimilarity.ContainsKey(GetPairKey(member, candidate)));

            if (connectedToAll)
            {
                group.Add(candidate);
            }
        }

        return group;
    }

    private static int CalculateGroupSimilarity(List<int> groupMembers, Dictionary<long, int> pairSimilarity)
    {
        var values = new List<int>();

        for (int i = 0; i < groupMembers.Count; i++)
        {
            for (int j = i + 1; j < groupMembers.Count; j++)
            {
                var key = GetPairKey(groupMembers[i], groupMembers[j]);
                if (pairSimilarity.TryGetValue(key, out var similarity))
                {
                    values.Add(similarity);
                }
            }
        }

        if (values.Count == 0)
        {
            return 100;
        }

        values.Sort();
        return values[values.Count / 2];
    }
}
