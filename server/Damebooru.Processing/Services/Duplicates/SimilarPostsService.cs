using Damebooru.Core.DTOs;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Duplicates;

public class SimilarPostsService
{
    // Deliberately looser than the duplicate threshold so variations of an image show up too.
    private const int MinSimilarityPercent = 50;
    private const int MaxResults = 30;

    private readonly DamebooruDbContext _context;

    public SimilarPostsService(DamebooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result<List<SimilarPostDto>>> GetSimilarPostsAsync(int postId, CancellationToken cancellationToken = default)
    {
        var target = await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id == postId)
            .Select(p => new { p.PdqHash256 })
            .FirstOrDefaultAsync(cancellationToken);
        if (target == null)
        {
            return Result<List<SimilarPostDto>>.Failure(OperationError.NotFound, "Post not found");
        }

        if (string.IsNullOrWhiteSpace(target.PdqHash256)
            || !PdqHashMatchHelper.TryParseHex256(target.PdqHash256, out var targetHash))
        {
            return Result<List<SimilarPostDto>>.Success([]);
        }

        var candidates = await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id != postId && p.ContentType.StartsWith("image/") && !string.IsNullOrEmpty(p.PdqHash256))
            .Select(p => new { p.Id, p.PdqHash256 })
            .ToListAsync(cancellationToken);

        var similarityById = new Dictionary<int, int>();
        foreach (var candidate in candidates)
        {
            if (PdqHashMatchHelper.TryParseHex256(candidate.PdqHash256!, out var candidateHash)
                && PdqHashMatchHelper.TryComputeSimilarity(targetHash, candidateHash, MinSimilarityPercent, out var similarity))
            {
                similarityById[candidate.Id] = similarity;
            }
        }

        var matchIds = similarityById
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(MaxResults)
            .Select(pair => pair.Key)
            .ToList();
        if (matchIds.Count == 0)
        {
            return Result<List<SimilarPostDto>>.Success([]);
        }

        var duplicateIds = (await _context.DuplicateGroupEntries
                .AsNoTracking()
                .Where(e => e.PostId != postId && e.DuplicateGroup.Entries.Any(other => other.PostId == postId))
                .Select(e => e.PostId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var posts = await _context.Posts
            .AsNoTracking()
            .Where(p => matchIds.Contains(p.Id))
            .Select(p => new SimilarPostDto
            {
                Id = p.Id,
                LibraryId = p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.LibraryId).FirstOrDefault(),
                LibraryName = p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.Library.Name).FirstOrDefault() ?? string.Empty,
                RelativePath = p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.RelativePath).FirstOrDefault() ?? string.Empty,
                ContentHash = p.ContentHash,
                Width = p.Width,
                Height = p.Height,
                SizeBytes = p.SizeBytes,
                ContentType = p.ContentType,
            })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var result = matchIds
            .Where(posts.ContainsKey)
            .Select(id =>
            {
                var post = posts[id];
                post.SimilarityPercent = similarityById[id];
                post.IsInDuplicateGroup = duplicateIds.Contains(id);
                return post;
            })
            .ToList();

        return Result<List<SimilarPostDto>>.Success(result);
    }
}
