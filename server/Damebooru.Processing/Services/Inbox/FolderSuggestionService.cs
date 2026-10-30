using System.Text.RegularExpressions;
using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

/// <summary>
/// Ranks library folders as destinations for a post, learning from where already sorted posts live.
/// Each folder collects evidence from the post's tags, combined so agreeing evidence adds up without exceeding 1:
/// <list type="bullet">
/// <item>Name match: the folder is named after one of the post's character, copyright or artist tags.</item>
/// <item>Vote: posts with one of the post's tags are concentrated in the folder, beyond what its size explains.</item>
/// </list>
/// Folder tags never count: they are the answer, not evidence.
/// </summary>
public partial class FolderSuggestionService
{
    private const double NameMatchConfidence = 0.99;
    // Damps votes from tags seen on only a few sorted posts: the weight is n / (n + SupportPrior).
    private const double SupportPrior = 2;
    // Tags on more sorted posts than this are too common to point anywhere, and slow to count.
    private const double MaxVotingTagShare = 0.10;
    private const int MinVotingTagLimit = 1000;
    // A subfolder this confident replaces its ancestors, so "Series/Character" wins over "Series".
    private const double ConfidentDescendantScore = 0.5;
    private const double MinScore = 0.05;
    private const int MaxSuggestions = 5;
    private const int MaxReasons = 3;

    private static readonly TagCategoryKind[] NamedCategories = [TagCategoryKind.Character, TagCategoryKind.Copyright, TagCategoryKind.Artist];

    private readonly DamebooruDbContext _context;

    public FolderSuggestionService(DamebooruDbContext context)
    {
        _context = context;
    }

    public async Task<List<PostFolderSuggestionsDto>> SuggestAsync(IReadOnlyCollection<int> postIds, CancellationToken cancellationToken)
    {
        var index = await LoadIndexAsync(postIds, cancellationToken);
        return postIds
            .Distinct()
            .Select(postId => new PostFolderSuggestionsDto { PostId = postId, Suggestions = index.Rank(postId, excludeFromLibrary: false) })
            .ToList();
    }

    /// <summary>
    /// Asks for suggestions for random sorted posts as if each were not sorted yet, and counts how often the real folder comes up.
    /// </summary>
    public async Task<FolderSuggestionEvaluationDto> EvaluateAsync(int sampleSize, CancellationToken cancellationToken)
    {
        var postIds = await _context.Posts
            .AsNoTracking()
            .WhereSorted()
            .Where(p => p.PostTags.Any(pt => pt.Source != PostTagSource.Folder))
            .OrderBy(_ => EF.Functions.Random())
            .Select(p => p.Id)
            .Take(sampleSize)
            .ToListAsync(cancellationToken);

        var index = await LoadIndexAsync(postIds, cancellationToken);
        var evaluation = new FolderSuggestionEvaluationDto { Sampled = postIds.Count };
        foreach (var postId in postIds)
        {
            var actual = index.FoldersOf(postId);
            var suggested = index.Rank(postId, excludeFromLibrary: true);
            var rank = suggested.FindIndex(s => actual.Contains(new FolderKey(s.LibraryId, s.Folder)));

            if (suggested.Count == 0)
            {
                evaluation.WithoutSuggestions++;
            }

            if (rank == 0)
            {
                evaluation.Top1Hits++;
            }

            if (rank is >= 0 and < 3)
            {
                evaluation.Top3Hits++;
            }
            else
            {
                evaluation.Misses.Add(new FolderSuggestionMissDto
                {
                    PostId = postId,
                    Actual = actual.Select(f => new FolderLocationDto { LibraryId = f.LibraryId, Folder = f.Path }).ToList(),
                    Suggested = suggested,
                });
            }
        }

        return evaluation;
    }

    private async Task<SuggestionIndex> LoadIndexAsync(IReadOnlyCollection<int> postIds, CancellationToken cancellationToken)
    {
        var sortedFiles = await _context.PostFiles
            .AsNoTracking()
            .Where(pf => pf.Library.Kind != LibraryKind.Inbox)
            .Select(pf => new { pf.PostId, pf.LibraryId, pf.RelativePath })
            .ToListAsync(cancellationToken);
        var foldersByPost = sortedFiles
            .GroupBy(f => f.PostId)
            .ToDictionary(g => g.Key, g => g.Select(f => new FolderKey(f.LibraryId, DirectoryOf(f.RelativePath))).ToHashSet());

        var postTags = await _context.PostTags
            .AsNoTracking()
            .Where(pt => postIds.Contains(pt.PostId) && pt.Source != PostTagSource.Folder)
            .Select(pt => new { pt.PostId, pt.Tag.Name, pt.Tag.Category, pt.Source })
            .ToListAsync(cancellationToken);
        var predictions = await _context.PostAiPredictions
            .AsNoTracking()
            .Where(p => postIds.Contains(p.PostId))
            .Select(p => new { p.PostId, p.TagName, p.Category, p.Score })
            .ToListAsync(cancellationToken);

        var weightsByPost = new Dictionary<int, Dictionary<string, PostTagWeight>>();
        void AddWeight(int postId, string name, TagCategoryKind category, double weight)
        {
            var weights = weightsByPost.TryGetValue(postId, out var existing) ? existing : weightsByPost[postId] = [];
            if (!weights.TryGetValue(name, out var current) || current.Weight < weight)
            {
                weights[name] = new PostTagWeight(name, category, weight);
            }
        }

        var predictionScores = predictions.ToDictionary(p => (p.PostId, p.TagName), p => (double)p.Score);
        foreach (var tag in postTags)
        {
            // Applied AI tags keep their prediction score when there is one; other sources are taken as certain.
            var weight = tag.Source == PostTagSource.Ai && predictionScores.TryGetValue((tag.PostId, tag.Name), out var score) ? score : 1.0;
            AddWeight(tag.PostId, tag.Name, tag.Category, weight);
        }

        foreach (var prediction in predictions)
        {
            AddWeight(prediction.PostId, prediction.TagName, prediction.Category, (double)prediction.Score);
        }

        var maxVotingPosts = Math.Max(MinVotingTagLimit, (int)(foldersByPost.Count * MaxVotingTagShare));
        var tagNames = weightsByPost.Values.SelectMany(w => w.Keys).Distinct().ToList();
        var votingTags = await _context.PostTags
            .AsNoTracking()
            .Where(pt => tagNames.Contains(pt.Tag.Name) && pt.Tag.PostCount <= maxVotingPosts && pt.Source != PostTagSource.Folder)
            .Select(pt => new { pt.Tag.Name, pt.PostId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var sortedPostsByTag = votingTags
            .Where(t => foldersByPost.ContainsKey(t.PostId))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Select(t => t.PostId).ToList());

        return new SuggestionIndex(foldersByPost, weightsByPost, sortedPostsByTag);
    }

    private static string DirectoryOf(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    /// <summary>
    /// Compares tag names with folder names. Folders can't contain characters like ':' on Windows,
    /// so "rem (re zero)" stands for the tag "rem_(re:zero)"; blanking those characters matches both.
    /// </summary>
    private static string NameKey(string name)
        => Tag.NormalizeName(FolderNameSeparatorRegex().Replace(name, "_")).TrimEnd('.', '_');

    [GeneratedRegex(@"[\s<>:""/\\|?*]+")]
    private static partial Regex FolderNameSeparatorRegex();

    private readonly record struct FolderKey(int LibraryId, string Path)
    {
        public bool IsAncestorOf(FolderKey other)
            => LibraryId == other.LibraryId
                && other.Path.Length > Path.Length
                && (Path.Length == 0 || other.Path.StartsWith(Path + "/", StringComparison.Ordinal));

        public IEnumerable<FolderKey> SelfAndAncestors()
        {
            for (var path = Path; ; path = DirectoryOf(path))
            {
                yield return this with { Path = path };
                if (path.Length == 0)
                {
                    yield break;
                }
            }
        }
    }

    private sealed record PostTagWeight(string Name, TagCategoryKind Category, double Weight);

    private sealed record Evidence(double Score, string Reason);

    private sealed class SuggestionIndex
    {
        private readonly Dictionary<int, HashSet<FolderKey>> _foldersByPost;
        private readonly Dictionary<int, Dictionary<string, PostTagWeight>> _weightsByPost;
        private readonly Dictionary<string, List<int>> _sortedPostsByTag;
        private readonly Dictionary<FolderKey, int> _postsPerFolder = [];
        private readonly Dictionary<FolderKey, int> _postsPerSubtree = [];
        private readonly Dictionary<string, List<FolderKey>> _foldersByName = [];

        public SuggestionIndex(
            Dictionary<int, HashSet<FolderKey>> foldersByPost,
            Dictionary<int, Dictionary<string, PostTagWeight>> weightsByPost,
            Dictionary<string, List<int>> sortedPostsByTag)
        {
            _foldersByPost = foldersByPost;
            _weightsByPost = weightsByPost;
            _sortedPostsByTag = sortedPostsByTag;

            foreach (var folders in foldersByPost.Values)
            {
                foreach (var folder in folders)
                {
                    _postsPerFolder[folder] = _postsPerFolder.GetValueOrDefault(folder) + 1;
                }

                foreach (var folder in folders.SelectMany(f => f.SelfAndAncestors()).Distinct())
                {
                    _postsPerSubtree[folder] = _postsPerSubtree.GetValueOrDefault(folder) + 1;
                }
            }

            foreach (var folder in _postsPerSubtree.Keys.Where(f => f.Path.Length > 0))
            {
                var name = NameKey(folder.Path[(folder.Path.LastIndexOf('/') + 1)..]);
                if (name.Length > 0)
                {
                    (_foldersByName.TryGetValue(name, out var list) ? list : _foldersByName[name] = []).Add(folder);
                }
            }
        }

        public HashSet<FolderKey> FoldersOf(int postId)
            => _foldersByPost.GetValueOrDefault(postId) ?? [];

        /// <param name="excludeFromLibrary">Rank a sorted post as if it were not in the library, so it can't vote for its own folder.</param>
        public List<FolderSuggestionDto> Rank(int postId, bool excludeFromLibrary)
        {
            if (!_weightsByPost.TryGetValue(postId, out var weights))
            {
                return [];
            }

            var excludedFolders = excludeFromLibrary ? FoldersOf(postId) : [];
            var excludedSubtrees = excludedFolders.SelectMany(f => f.SelfAndAncestors()).ToHashSet();
            var totalPosts = _foldersByPost.Count - (excludedFolders.Count > 0 ? 1 : 0);
            if (totalPosts == 0)
            {
                return [];
            }

            int PostsIn(FolderKey folder) => _postsPerFolder.GetValueOrDefault(folder) - (excludedFolders.Contains(folder) ? 1 : 0);
            int PostsUnder(FolderKey folder) => _postsPerSubtree.GetValueOrDefault(folder) - (excludedSubtrees.Contains(folder) ? 1 : 0);

            var evidence = new Dictionary<FolderKey, List<Evidence>>();
            void Add(FolderKey folder, double score, string reason)
                => (evidence.TryGetValue(folder, out var list) ? list : evidence[folder] = []).Add(new Evidence(score, reason));

            foreach (var tag in weights.Values)
            {
                if (NamedCategories.Contains(tag.Category) && _foldersByName.TryGetValue(NameKey(tag.Name), out var namedFolders))
                {
                    foreach (var folder in namedFolders.Where(f => PostsUnder(f) > 0))
                    {
                        Add(folder, NameMatchConfidence * tag.Weight, $"Named after {tag.Category.ToString().ToLowerInvariant()} tag \"{tag.Name}\" ({tag.Weight:P0})");
                    }
                }

                if (!_sortedPostsByTag.TryGetValue(tag.Name, out var taggedPosts))
                {
                    continue;
                }

                var votes = taggedPosts
                    .Where(id => !excludeFromLibrary || id != postId)
                    .SelectMany(id => _foldersByPost[id])
                    .GroupBy(folder => folder)
                    .ToList();
                var tagged = taggedPosts.Count - (excludeFromLibrary && taggedPosts.Contains(postId) ? 1 : 0);
                if (tagged == 0)
                {
                    continue;
                }

                var support = tagged / (tagged + SupportPrior);
                foreach (var vote in votes)
                {
                    var share = (double)vote.Count() / tagged;
                    var baseShare = (double)PostsIn(vote.Key) / totalPosts;
                    if (share <= baseShare || baseShare >= 1)
                    {
                        continue;
                    }

                    var lift = (share - baseShare) / (1 - baseShare);
                    Add(vote.Key, tag.Weight * support * lift, $"{vote.Count()}/{tagged} posts tagged \"{tag.Name}\" are here");
                }
            }

            var scored = evidence
                .Select(e => (Folder: e.Key, Score: 1 - e.Value.Aggregate(1.0, (rest, item) => rest * (1 - item.Score)), Evidence: e.Value))
                .Where(s => s.Score >= MinScore)
                .ToList();

            return scored
                .Where(s => !scored.Any(other => s.Folder.IsAncestorOf(other.Folder) && other.Score >= ConfidentDescendantScore))
                .OrderByDescending(s => s.Score)
                .Take(MaxSuggestions)
                .Select(s => new FolderSuggestionDto
                {
                    LibraryId = s.Folder.LibraryId,
                    Folder = s.Folder.Path,
                    Score = s.Score,
                    Reasons = s.Evidence.OrderByDescending(e => e.Score).Take(MaxReasons).Select(e => e.Reason).ToList(),
                })
                .ToList();
        }
    }
}
