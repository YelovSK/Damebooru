using Damebooru.Core.Entities;
using Damebooru.Core.DTOs;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Extensions;
using Damebooru.Processing.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Damebooru.Processing.Services;

public class PostReadService
{
    private const int MaxPageSize = 500;
    private const int MaxAroundWindowSize = 100;
    private const int MaxAroundIdWindowSize = 100;

    private readonly DamebooruDbContext _context;
    private readonly ILogger<PostReadService> _logger;

    public PostReadService(DamebooruDbContext context, ILogger<PostReadService>? logger = null)
    {
        _context = context;
        _logger = logger ?? NullLogger<PostReadService>.Instance;
    }

    public async Task<Result<PostListDto>> GetPostsAsync(string? tags, int offset, int limit, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        if (offset < 0) offset = 0;
        if (limit < 1) limit = 20;
        if (limit > MaxPageSize) limit = MaxPageSize;

        var filterStopwatch = Stopwatch.StartNew();
        var parsedQuery = QueryParser.Parse(tags ?? string.Empty);
        var query = await ApplySearchFiltersAsync(_context.Posts.AsNoTracking(), parsedQuery, cancellationToken);
        filterStopwatch.Stop();

        var countStopwatch = Stopwatch.StartNew();
        var totalCount = await query.CountAsync(cancellationToken);
        countStopwatch.Stop();

        var itemsStopwatch = Stopwatch.StartNew();
        var items = await query
            .ApplySorting(parsedQuery)
            .Skip(offset)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.ImportDate,
                p.IsFavorite,
                RepresentativeFile = p.PrimaryPostFile == null
                    ? null
                    : new
                    {
                        p.PrimaryPostFile.LibraryId,
                        p.PrimaryPostFile.RelativePath,
                        p.PrimaryPostFile.ContentHash,
                        p.PrimaryPostFile.SizeBytes,
                        p.PrimaryPostFile.Width,
                        p.PrimaryPostFile.Height,
                        p.PrimaryPostFile.ContentType,
                        p.PrimaryPostFile.FileModifiedDate,
                    },
                PostFiles = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => new PostFileDto
                    {
                        LibraryId = pf.LibraryId,
                        LibraryName = null,
                        RelativePath = pf.RelativePath,
                        ContentHash = pf.ContentHash,
                        SizeBytes = pf.SizeBytes,
                        Width = pf.Width,
                        Height = pf.Height,
                        ContentType = pf.ContentType,
                        FileModifiedDate = pf.FileModifiedDate,
                    })
                    .ToList(),
            })
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.LibraryId,
                RelativePath = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.RelativePath,
                ContentHash = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.ContentHash,
                SizeBytes = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.SizeBytes,
                Width = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.Width,
                Height = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.Height,
                ContentType = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.ContentType,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.RepresentativeFile == null ? default : p.RepresentativeFile.FileModifiedDate,
                IsFavorite = p.IsFavorite,
                Sources = new List<string>(),
                PostFiles = p.PostFiles,
                ThumbnailLibraryId = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.LibraryId,
                ThumbnailContentHash = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.ContentHash,
                Tags = new List<TagDto>(),
            })
            .ToListAsync(cancellationToken);
        itemsStopwatch.Stop();
        totalStopwatch.Stop();

        _logger.LogInformation(
            "Posts list query completed in {ElapsedMs} ms: offset {Offset}, limit {Limit}, returned {ReturnedCount}, total {TotalCount}, filter {FilterMs} ms, count {CountMs} ms, items {ItemsMs} ms, query length {QueryLength}, sort {SortField} {SortDirection}",
            totalStopwatch.ElapsedMilliseconds,
            offset,
            limit,
            items.Count,
            totalCount,
            filterStopwatch.ElapsedMilliseconds,
            countStopwatch.ElapsedMilliseconds,
            itemsStopwatch.ElapsedMilliseconds,
            tags?.Length ?? 0,
            parsedQuery.SortField,
            parsedQuery.SortDirection);

        return Result<PostListDto>.Success(new PostListDto
        {
            Items = items,
            TotalCount = totalCount,
            Offset = offset,
            Limit = limit
        });
    }

    public async Task<Result<PostDto>> GetPostAsync(int id, CancellationToken cancellationToken)
    {
        var post = await PostDtoLoader.LoadPostAsync(_context, id, cancellationToken);
        if (post == null)
        {
            return Result<PostDto>.Failure(OperationError.NotFound, "Post not found");
        }

        return Result<PostDto>.Success(post);
    }

    public async Task<Result<PostsAroundDto>> GetPostsAroundAsync(
        int id,
        string? tags,
        int window,
        int? before,
        int? after,
        CancellationToken cancellationToken)
    {
        var beforeSize = NormalizeAroundWindowSize(before ?? window);
        var afterSize = NormalizeAroundWindowSize(after ?? window);
        var idWindowSize = Math.Min(Math.Max(beforeSize, afterSize) + 1, MaxAroundIdWindowSize);
        var idsResult = await GetPostIdsAroundAsync(id, tags, idWindowSize, cancellationToken);
        if (!idsResult.IsSuccess)
        {
            return Result<PostsAroundDto>.Failure(
                idsResult.Error ?? OperationError.InvalidInput,
                idsResult.Message ?? "Failed to load surrounding posts.");
        }

        var ids = idsResult.Value!;
        var prevIds = ids.PrevIds.Take(beforeSize).ToList();
        var nextIds = ids.NextIds.Take(afterSize).ToList();
        var postsById = await PostDtoLoader.LoadPostsByIdAsync(_context, prevIds.Concat([id]).Concat(nextIds), cancellationToken);
        if (!postsById.TryGetValue(id, out var currentPost))
        {
            return Result<PostsAroundDto>.Failure(OperationError.NotFound, "Post not found");
        }

        var prevItems = prevIds
            .Where(postsById.ContainsKey)
            .Select(postId => postsById[postId])
            .ToList();
        var nextItems = nextIds
            .Where(postsById.ContainsKey)
            .Select(postId => postsById[postId])
            .ToList();
        var orderedItems = prevItems
            .AsEnumerable()
            .Reverse()
            .Concat([currentPost])
            .Concat(nextItems)
            .ToList();

        return Result<PostsAroundDto>.Success(new PostsAroundDto
        {
            Prev = prevItems.FirstOrDefault(),
            Next = nextItems.FirstOrDefault(),
            PrevItems = prevItems,
            NextItems = nextItems,
            Items = orderedItems,
            AnchorIndex = prevItems.Count,
            HasPrevious = ids.PrevIds.Count > beforeSize,
            HasNext = ids.NextIds.Count > afterSize,
        });
    }

    private async Task<Result<AroundPostIds>> GetPostIdsAroundAsync(
        int id,
        string? tags,
        int window,
        CancellationToken cancellationToken)
    {
        var windowSize = NormalizeAroundIdWindowSize(window);
        var parsedQuery = QueryParser.Parse(tags ?? string.Empty);

        var current = await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id,
                FileModifiedDate = p.PrimaryFileModifiedDate ?? default(DateTime)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
        {
            return Result<AroundPostIds>.Failure(OperationError.NotFound, "Post not found");
        }

        var currentSortDate = current.FileModifiedDate;

        var query = await ApplySearchFiltersAsync(_context.Posts.AsNoTracking(), parsedQuery, cancellationToken);

        var prevIds = await query
            .Where(p => p.Id != current.Id
                && (
                    (p.PrimaryFileModifiedDate ?? default(DateTime)) > currentSortDate
                    || ((p.PrimaryFileModifiedDate ?? default(DateTime)) == currentSortDate && p.Id > current.Id)
                ))
            .OrderByOldest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        var nextIds = await query
            .Where(p => p.Id != current.Id
                && (
                    (p.PrimaryFileModifiedDate ?? default(DateTime)) < currentSortDate
                    || ((p.PrimaryFileModifiedDate ?? default(DateTime)) == currentSortDate && p.Id < current.Id)
                ))
            .OrderByNewest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        return Result<AroundPostIds>.Success(new AroundPostIds(prevIds, nextIds));
    }

    public async Task<Result<PostAuditListDto>> GetPostAuditAsync(int postId, long? beforeId, int take, CancellationToken cancellationToken)
    {
        var postExists = await _context.Posts
            .AsNoTracking()
            .AnyAsync(p => p.Id == postId, cancellationToken);
        if (!postExists)
        {
            return Result<PostAuditListDto>.Failure(OperationError.NotFound, "Post not found");
        }

        var normalizedTake = Math.Clamp(take, 1, 200);

        var query = _context.PostAuditEntries
            .AsNoTracking()
            .Where(e => e.PostId == postId);

        if (beforeId.HasValue)
        {
            query = query.Where(e => e.Id < beforeId.Value);
        }

        var items = await query
            .OrderByDescending(e => e.Id)
            .Take(normalizedTake + 1)
            .Select(e => new PostAuditEntryDto
            {
                Id = e.Id,
                OccurredAtUtc = e.OccurredAtUtc,
                Entity = e.Entity,
                Operation = e.Operation,
                Field = e.Field,
                OldValue = e.OldValue,
                NewValue = e.NewValue,
            })
            .ToListAsync(cancellationToken);

        var hasMore = items.Count > normalizedTake;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return Result<PostAuditListDto>.Success(new PostAuditListDto
        {
            Items = items,
            HasMore = hasMore,
        });
    }

    private static int NormalizeAroundWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundWindowSize);

    private static int NormalizeAroundIdWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundIdWindowSize);

    private sealed record AroundPostIds(IReadOnlyList<int> PrevIds, IReadOnlyList<int> NextIds);

    private async Task<IQueryable<Post>> ApplySearchFiltersAsync(IQueryable<Post> query, SearchQuery parsedQuery, CancellationToken cancellationToken)
    {
        var includedTags = parsedQuery.IncludedTags.Distinct().ToList();
        var excludedTags = parsedQuery.ExcludedTags.Distinct().ToList();
        var includedFilenames = parsedQuery.IncludedFilenames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var excludedFilenames = parsedQuery.ExcludedFilenames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allTagNames = includedTags.Concat(excludedTags).Distinct().ToList();

        if (allTagNames.Count > 0)
        {
            var tagIdsByName = await _context.Tags
                .Where(t => allTagNames.Contains(t.Name))
                .Select(t => new { t.Name, t.Id })
                .ToDictionaryAsync(t => t.Name, t => t.Id, cancellationToken);

            if (includedTags.Any(tag => !tagIdsByName.ContainsKey(tag)))
            {
                return query.Where(_ => false);
            }

            foreach (var tag in includedTags)
            {
                var tagId = tagIdsByName[tag];
                query = query.Where(p => p.PostTags.Any(pt => pt.TagId == tagId));
            }

            foreach (var tag in excludedTags)
            {
                if (!tagIdsByName.TryGetValue(tag, out var tagId))
                {
                    continue;
                }

                query = query.Where(p => !p.PostTags.Any(pt => pt.TagId == tagId));
            }
        }

        if (parsedQuery.IncludedMediaTypes.Count > 0)
        {
            var includeImage = parsedQuery.IncludedMediaTypes.Contains(PostMediaType.Image);
            var includeAnimation = parsedQuery.IncludedMediaTypes.Contains(PostMediaType.Animation);
            var includeVideo = parsedQuery.IncludedMediaTypes.Contains(PostMediaType.Video);

            query = query.Where(p =>
                (includeImage && p.PostFiles.Any(pf => pf.ContentType.StartsWith("image/") && pf.ContentType != "image/gif"))
                || (includeAnimation && p.PostFiles.Any(pf => pf.ContentType == "image/gif"))
                || (includeVideo && p.PostFiles.Any(pf => pf.ContentType.StartsWith("video/"))));
        }

        if (parsedQuery.ExcludedMediaTypes.Count > 0)
        {
            if (parsedQuery.ExcludedMediaTypes.Contains(PostMediaType.Image))
            {
                query = query.Where(p => !p.PostFiles.Any(pf => pf.ContentType.StartsWith("image/") && pf.ContentType != "image/gif"));
            }

            if (parsedQuery.ExcludedMediaTypes.Contains(PostMediaType.Animation))
            {
                query = query.Where(p => !p.PostFiles.Any(pf => pf.ContentType == "image/gif"));
            }

            if (parsedQuery.ExcludedMediaTypes.Contains(PostMediaType.Video))
            {
                query = query.Where(p => !p.PostFiles.Any(pf => pf.ContentType.StartsWith("video/")));
            }
        }

        if (parsedQuery.TagCountFilter != null)
        {
            var count = parsedQuery.TagCountFilter.Value;
            query = parsedQuery.TagCountFilter.Operator switch
            {
                NumericComparisonOperator.Equal => query.Where(p => p.PostTags.Select(pt => pt.TagId).Distinct().Count() == count),
                NumericComparisonOperator.GreaterThan => query.Where(p => p.PostTags.Select(pt => pt.TagId).Distinct().Count() > count),
                NumericComparisonOperator.GreaterThanOrEqual => query.Where(p => p.PostTags.Select(pt => pt.TagId).Distinct().Count() >= count),
                NumericComparisonOperator.LessThan => query.Where(p => p.PostTags.Select(pt => pt.TagId).Distinct().Count() < count),
                NumericComparisonOperator.LessThanOrEqual => query.Where(p => p.PostTags.Select(pt => pt.TagId).Distinct().Count() <= count),
                _ => query
            };
        }

        if (parsedQuery.FavoriteFilter.HasValue)
        {
            var isFavorite = parsedQuery.FavoriteFilter.Value;
            query = query.Where(p => p.IsFavorite == isFavorite);
        }

        foreach (var filename in includedFilenames)
        {
            var pattern = BuildFilenamePattern(filename);
            query = query.Where(p => p.PostFiles.Any(pf => EF.Functions.Like(pf.RelativePath, pattern)));
        }

        foreach (var filename in excludedFilenames)
        {
            var pattern = BuildFilenamePattern(filename);
            query = query.Where(p => !p.PostFiles.Any(pf => EF.Functions.Like(pf.RelativePath, pattern)));
        }

        return query;
    }

    private static string BuildFilenamePattern(string rawFilenameFilter)
    {
        var filter = rawFilenameFilter.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            return "%";
        }

        if (filter.Contains('*') || filter.Contains('?'))
        {
            return filter.Replace('*', '%').Replace('?', '_');
        }

        return $"%{filter}%";
    }
}
