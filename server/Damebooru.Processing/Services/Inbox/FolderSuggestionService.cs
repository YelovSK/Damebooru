using System.Text.RegularExpressions;
using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

/// <summary>
/// Ranks library folders as destinations for a post by name: a folder named after one of the post's
/// character, copyright or artist tags is a match, scored by how sure the tag is.
/// Folder tags never count: they are the answer, not evidence.
/// </summary>
public partial class FolderSuggestionService
{
    private const double NameMatchConfidence = 0.99;
    // A subfolder this confident replaces its ancestors, so "Series/Character" wins over "Series".
    private const double ConfidentDescendantScore = 0.5;
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
        var needsPrediction = (await _context.Posts
                .AsNoTracking()
                .Where(p => postIds.Contains(p.Id) && p.AiPredictionDate == null && EF.Functions.Like(p.ContentType, "image/%"))
                .Select(p => p.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return postIds
            .Distinct()
            .Select(postId =>
            {
                var existing = index.FoldersOf(postId);
                return new PostFolderSuggestionsDto
                {
                    PostId = postId,
                    ExistingLocations = existing.Select(f => new FolderLocationDto { LibraryId = f.LibraryId, Folder = f.Path }).ToList(),
                    NeedsPrediction = existing.Count == 0 && needsPrediction.Contains(postId),
                    Suggestions = existing.Count == 0 ? index.Rank(postId, excludeFromLibrary: false) : [],
                };
            })
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
            .Where(p => p.PostTags.Any(pt => pt.Source != PostTagSource.Folder && NamedCategories.Contains(pt.Tag.Category)))
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
            .Where(pt => postIds.Contains(pt.PostId) && pt.Source != PostTagSource.Folder && NamedCategories.Contains(pt.Tag.Category))
            .Select(pt => new { pt.PostId, pt.Tag.Name, pt.Tag.Category, pt.Source })
            .ToListAsync(cancellationToken);
        var predictions = await _context.PostAiPredictions
            .AsNoTracking()
            .Where(p => postIds.Contains(p.PostId) && NamedCategories.Contains(p.Category))
            .Select(p => new { p.PostId, p.TagName, p.Category, p.Score })
            .ToListAsync(cancellationToken);

        var tagsByPost = new Dictionary<int, Dictionary<string, NamedTag>>();
        void Add(int postId, string name, TagCategoryKind category, double confidence)
        {
            var tags = tagsByPost.TryGetValue(postId, out var existing) ? existing : tagsByPost[postId] = [];
            if (!tags.TryGetValue(name, out var current) || current.Confidence < confidence)
            {
                tags[name] = new NamedTag(name, category, confidence);
            }
        }

        var predictionScores = predictions.ToDictionary(p => (p.PostId, p.TagName), p => (double)p.Score);
        foreach (var tag in postTags)
        {
            // Applied AI tags keep their prediction score when there is one; other sources are taken as certain.
            var confidence = tag.Source == PostTagSource.Ai && predictionScores.TryGetValue((tag.PostId, tag.Name), out var score) ? score : 1.0;
            Add(tag.PostId, tag.Name, tag.Category, confidence);
        }

        foreach (var prediction in predictions)
        {
            Add(prediction.PostId, prediction.TagName, prediction.Category, (double)prediction.Score);
        }

        return new SuggestionIndex(foldersByPost, tagsByPost);
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

    private sealed record NamedTag(string Name, TagCategoryKind Category, double Confidence);

    private sealed record Match(double Score, string Reason);

    private sealed class SuggestionIndex
    {
        private readonly Dictionary<int, HashSet<FolderKey>> _foldersByPost;
        private readonly Dictionary<int, Dictionary<string, NamedTag>> _tagsByPost;
        private readonly Dictionary<FolderKey, int> _postsPerSubtree = [];
        private readonly Dictionary<string, List<FolderKey>> _foldersByName = [];

        public SuggestionIndex(
            Dictionary<int, HashSet<FolderKey>> foldersByPost,
            Dictionary<int, Dictionary<string, NamedTag>> tagsByPost)
        {
            _foldersByPost = foldersByPost;
            _tagsByPost = tagsByPost;

            foreach (var folder in foldersByPost.Values.SelectMany(folders => folders.SelectMany(f => f.SelfAndAncestors()).Distinct()))
            {
                _postsPerSubtree[folder] = _postsPerSubtree.GetValueOrDefault(folder) + 1;
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

        /// <param name="excludeFromLibrary">Rank a sorted post as if it were not in the library, so folders only it lives in don't count.</param>
        public List<FolderSuggestionDto> Rank(int postId, bool excludeFromLibrary)
        {
            if (!_tagsByPost.TryGetValue(postId, out var tags))
            {
                return [];
            }

            var excludedSubtrees = excludeFromLibrary ? FoldersOf(postId).SelectMany(f => f.SelfAndAncestors()).ToHashSet() : [];
            int PostsUnder(FolderKey folder) => _postsPerSubtree.GetValueOrDefault(folder) - (excludedSubtrees.Contains(folder) ? 1 : 0);

            var matches = new Dictionary<FolderKey, List<Match>>();
            foreach (var tag in tags.Values)
            {
                if (!_foldersByName.TryGetValue(NameKey(tag.Name), out var namedFolders))
                {
                    continue;
                }

                foreach (var folder in namedFolders.Where(f => PostsUnder(f) > 0))
                {
                    (matches.TryGetValue(folder, out var list) ? list : matches[folder] = []).Add(new Match(
                        NameMatchConfidence * tag.Confidence,
                        $"{tag.Category} tag \"{tag.Name}\" ({tag.Confidence:P0})"));
                }
            }

            var scored = matches
                .Select(m => (Folder: m.Key, Score: m.Value.Max(x => x.Score), Matches: m.Value))
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
                    Reasons = s.Matches.OrderByDescending(m => m.Score).Take(MaxReasons).Select(m => m.Reason).ToList(),
                })
                .ToList();
        }
    }
}
