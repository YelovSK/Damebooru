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
    public const string JobKey = "find-duplicates";
    public const string JobName = "Find Duplicates";

    private sealed record HashPost(int Id, ulong? DHash, ulong? PHash, string ContentType);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FindDuplicatesJob> _logger;

    /// <summary>
    /// Independent perceptual thresholds (out of 64 bits) for single-signal fallback.
    /// </summary>
    public int DHashThreshold { get; set; } = 8;
    public int PHashThreshold { get; set; } = 10;

    /// <summary>
    /// When both dHash and pHash are present, require at least this blended similarity.
    /// </summary>
    public int CombinedSimilarityThresholdPercent { get; set; } = 80;

    public FindDuplicatesJob(IServiceScopeFactory scopeFactory, ILogger<FindDuplicatesJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 40;
    public string Key => JobKey;
    public string Name => JobName;
    public string Description => "Scans for exact (content hash) and perceptual (dHash+pHash) duplicate posts.";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        context.State.Report(new JobState
        {
            Phase = "Loading posts..."
        });
        var posts = await db.Posts
            .AsNoTracking()
            .Select(p => new { p.Id, p.ContentHash, p.PerceptualHash, p.PerceptualHashP, p.ContentType })
            .ToListAsync(context.CancellationToken);
        context.State.Report(new JobState
        {
            Phase = "Loading posts...",
            Processed = posts.Count,
            Total = posts.Count,
            Summary = $"Loaded {posts.Count} posts for duplicate analysis"
        });

        _logger.LogInformation("Loaded {Count} posts for duplicate analysis", posts.Count);

        // Clear old unresolved groups (they'll be regenerated)
        context.State.Report(new JobState
        {
            Phase = "Clearing old unresolved groups..."
        });
        var oldGroups = await db.DuplicateGroups
            .Where(g => !g.IsResolved)
            .ToListAsync(context.CancellationToken);
        db.DuplicateGroups.RemoveRange(oldGroups);
        await db.SaveChangesAsync(context.CancellationToken);
        context.State.Report(new JobState
        {
            Phase = "Clearing old unresolved groups...",
            Processed = oldGroups.Count,
            Total = oldGroups.Count,
            Summary = $"Cleared {oldGroups.Count} old unresolved groups"
        });

        var newGroups = new List<DuplicateGroup>();

        // Load all existing resolved groups to skip recreating them
        context.State.Report(new JobState
        {
            Phase = "Loading resolved groups..."
        });
        
        var resolvedGroups = await db.DuplicateGroups
            .Where(g => g.IsResolved)
            .Include(g => g.Entries)
            .ToListAsync(context.CancellationToken);

        // Build a HashSet of strings representing sorted post IDs for easy lookup
        var resolvedGroupSignatures = new HashSet<string>();
        foreach (var group in resolvedGroups)
        {
            var signature = string.Join(",", group.Entries.Select(e => e.PostId).OrderBy(id => id));
            resolvedGroupSignatures.Add(signature);
        }

        // --- Phase 1: Exact duplicates (same content hash) ---
        context.State.Report(new JobState
        {
            Phase = "Finding exact duplicates...",
            Processed = 0,
            Total = posts.Count,
            Summary = "Grouping by content hash"
        });

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
                continue; // Skip this exact match because it was previously resolved
            }

            var dupGroup = new DuplicateGroup
            {
                Type = "exact",
                DetectedDate = DateTime.UtcNow,
                Entries = postIds.Select(id => new DuplicateGroupEntry { PostId = id }).ToList()
            };
            newGroups.Add(dupGroup);
        }

        _logger.LogInformation("Found {Count} exact duplicate groups", newGroups.Count);
        context.State.Report(new JobState
        {
            Phase = "Finding exact duplicates...",
            Processed = posts.Count,
            Total = posts.Count,
            Summary = $"Exact duplicate groups: {newGroups.Count}"
        });

        // --- Phase 2: Perceptual duplicates (independent dHash + pHash signals) ---
        context.State.Report(new JobState
        {
            Phase = "Finding perceptual duplicates...",
            Processed = 0,
            Total = null
        });

        var hashPosts = posts
            .Where(p =>
                (p.PerceptualHash.HasValue && p.PerceptualHash.Value != 0) ||
                (p.PerceptualHashP.HasValue && p.PerceptualHashP.Value != 0))
            .Select(p => new HashPost(p.Id, p.PerceptualHash, p.PerceptualHashP, p.ContentType))
            .ToList();

        _logger.LogInformation("Comparing {Count} perceptual hashes", hashPosts.Count);

        // Collect pairwise matches in an adjacency graph.
        // We later form strict groups where every member matches every other member (clique-like),
        // which avoids chain-link false positives (A~B, B~C, but A !~ C).
        var neighbors = new Dictionary<int, HashSet<int>>();
        var pairSimilarity = new Dictionary<long, int>();

        int totalComparisons = hashPosts.Count * (hashPosts.Count - 1) / 2;
        int comparedSoFar = 0;
        int lastReportedPercent = 30;
        int matchedPairs = 0;

        // Exclude post IDs that are already in exact duplicate groups
        var exactPostIds = new HashSet<int>(newGroups.SelectMany(g => g.Entries.Select(e => e.PostId)));

        for (int i = 0; i < hashPosts.Count; i++)
        {
            for (int j = i + 1; j < hashPosts.Count; j++)
            {
                comparedSoFar++;

                if (TryComputeSimilarity(hashPosts[i], hashPosts[j], out var similarity))
                {
                    var idA = hashPosts[i].Id;
                    var idB = hashPosts[j].Id;

                    // Skip if both are already grouped as exact duplicates
                    if (exactPostIds.Contains(idA) && exactPostIds.Contains(idB))
                        continue;

                    AddEdge(neighbors, idA, idB);
                    pairSimilarity[GetPairKey(idA, idB)] = similarity;
                    matchedPairs++;
                }

                // Progress reporting (30% -> 90%)
                if (totalComparisons > 0)
                {
                    int percent = 30 + (int)((double)comparedSoFar / totalComparisons * 60);
                    if (percent > lastReportedPercent + 2) // avoid too-frequent updates
                    {
                        lastReportedPercent = percent;
                        context.State.Report(new JobState
                        {
                            Phase = "Comparing perceptual hashes...",
                            Processed = comparedSoFar,
                            Total = totalComparisons,
                            Summary = $"Matched pairs so far: {matchedPairs}"
                        });
                    }
                }
            }
        }

        int perceptualCount = 0;
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
                // Skip because we already resolved this exact group of posts
                foreach (var member in groupMembers)
                {
                    remaining.Remove(member);
                }
                continue;
            }

            var similarity = CalculateGroupSimilarity(groupMembers, pairSimilarity);

            var dupGroup = new DuplicateGroup
            {
                Type = "perceptual",
                SimilarityPercent = similarity,
                DetectedDate = DateTime.UtcNow,
                Entries = postIds.Select(id => new DuplicateGroupEntry { PostId = id }).ToList()
            };

            newGroups.Add(dupGroup);
            perceptualCount++;

            foreach (var member in groupMembers)
            {
                remaining.Remove(member);
            }
        }

        _logger.LogInformation("Found {Count} perceptual duplicate groups", perceptualCount);
        context.State.Report(new JobState
        {
            Phase = "Finding perceptual duplicates...",
            Processed = totalComparisons,
            Total = totalComparisons,
            Summary = $"Perceptual duplicate groups: {perceptualCount}"
        });

        // --- Phase 3: Save results ---
        context.State.Report(new JobState
        {
            Phase = "Saving duplicate groups..."
        });

        if (newGroups.Count > 0)
        {
            db.DuplicateGroups.AddRange(newGroups);
            await db.SaveChangesAsync(context.CancellationToken);
        }

        var totalEntries = newGroups.Sum(g => g.Entries.Count);
        context.State.Report(new JobState
        {
            Phase = "Completed",
            Processed = totalEntries,
            Total = totalEntries,
            Succeeded = newGroups.Count,
            Summary = $"Found {newGroups.Count} duplicate groups ({totalEntries} posts)."
        });
        _logger.LogInformation("Duplicate scan complete: {Groups} groups, {Entries} total posts",
            newGroups.Count, totalEntries);
    }

    private bool TryComputeSimilarity(HashPost a, HashPost b, out int similarityPercent)
    {
        var dDistance = HammingDistanceNullable(a.DHash, b.DHash);
        var pDistance = HammingDistanceNullable(a.PHash, b.PHash);

        if (!dDistance.HasValue && !pDistance.HasValue)
        {
            similarityPercent = 0;
            return false;
        }

        var dSimilarity = dDistance.HasValue ? 1.0 - (double)dDistance.Value / 64 : (double?)null;
        var pSimilarity = pDistance.HasValue ? 1.0 - (double)pDistance.Value / 64 : (double?)null;

        var threshold = CombinedSimilarityThresholdPercent / 100.0;

        // Require higher similarity for cross-type comparison or video-video
        // because video frames at 20% mark might accidentally look similar to other frames/images
        var isAImage = a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isBImage = b.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        if (!isAImage || !isBImage)
        {
            threshold = Math.Max(threshold, 0.90);
        }

        if (dSimilarity.HasValue && pSimilarity.HasValue)
        {
            // If both signals exist, require blended agreement to avoid one-sided false positives.
            var combinedSimilarity = (dSimilarity.Value * 0.55) + (pSimilarity.Value * 0.45);
            if (combinedSimilarity < threshold)
            {
                similarityPercent = 0;
                return false;
            }

            similarityPercent = (int)Math.Round(combinedSimilarity * 100);
            return true;
        }

        // Single-signal fallback for partial/missing data.
        // We only allow this if both are the same media type (images)
        if (isAImage && isBImage)
        {
            if (dDistance.HasValue && dDistance.Value <= DHashThreshold)
            {
                similarityPercent = (int)Math.Round((1.0 - (double)dDistance.Value / 64) * 100);
                return true;
            }

            if (pDistance.HasValue && pDistance.Value <= PHashThreshold)
            {
                similarityPercent = (int)Math.Round((1.0 - (double)pDistance.Value / 64) * 100);
                return true;
            }
        }

        similarityPercent = 0;
        return false;
    }

    private static int? HammingDistanceNullable(ulong? a, ulong? b)
    {
        if (!a.HasValue || !b.HasValue || a.Value == 0 || b.Value == 0)
        {
            return null;
        }

        return HammingDistance(a.Value, b.Value);
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        return BitOperations.PopCount(a ^ b);
    }

    private static void AddEdge(Dictionary<int, HashSet<int>> neighbors, int a, int b)
    {
        if (!neighbors.TryGetValue(a, out var setA))
        {
            setA = new HashSet<int>();
            neighbors[a] = setA;
        }

        if (!neighbors.TryGetValue(b, out var setB))
        {
            setB = new HashSet<int>();
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

        int degree = 0;
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
            bool connectedToAll = true;
            foreach (var member in group)
            {
                if (member == candidate)
                {
                    continue;
                }

                var key = GetPairKey(member, candidate);
                if (!pairSimilarity.ContainsKey(key))
                {
                    connectedToAll = false;
                    break;
                }
            }

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
