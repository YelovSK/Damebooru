using Damebooru.Core.Entities;
using Damebooru.Core.DTOs;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Extensions;
using Damebooru.Processing.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class PostReadService
{
    private const int MaxPageSize = 500;
    private const int MaxAroundWindowSize = 100;
    private const int MaxAroundIdWindowSize = 100;

    private readonly DamebooruDbContext _context;

    public PostReadService(DamebooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result<PostListDto>> GetPostsAsync(string? tags, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var parsedQuery = QueryParser.Parse(tags ?? string.Empty);
        var query = await ApplySearchFiltersAsync(_context.Posts.AsNoTracking(), parsedQuery, cancellationToken);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .ApplySorting(parsedQuery)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.LibraryId)
                    .FirstOrDefault(),
                RelativePath = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.RelativePath)
                    .FirstOrDefault() ?? string.Empty,
                ContentHash = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.ContentHash)
                    .FirstOrDefault() ?? string.Empty,
                SizeBytes = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => (long?)pf.SizeBytes)
                    .FirstOrDefault() ?? 0,
                Width = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => (int?)pf.Width)
                    .FirstOrDefault() ?? 0,
                Height = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => (int?)pf.Height)
                    .FirstOrDefault() ?? 0,
                ContentType = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.ContentType)
                    .FirstOrDefault() ?? string.Empty,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => (DateTime?)pf.FileModifiedDate)
                    .FirstOrDefault() ?? default,
                IsFavorite = p.IsFavorite,
                Sources = new List<string>(),
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
                ThumbnailLibraryId = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.LibraryId)
                    .FirstOrDefault(),
                ThumbnailContentHash = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.ContentHash)
                    .FirstOrDefault() ?? string.Empty,
                Tags = new List<TagDto>(),
            })
            .ToListAsync(cancellationToken);

        return Result<PostListDto>.Success(new PostListDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    public async Task<Result<PostDto>> GetPostAsync(int id, CancellationToken cancellationToken)
    {
        var post = await LoadPostAsync(id, cancellationToken);
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
        var postsById = await LoadPostsByIdAsync(prevIds.Concat([id]).Concat(nextIds), cancellationToken);
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
                FileModifiedDate = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => (DateTime?)pf.FileModifiedDate)
                    .FirstOrDefault() ?? default(DateTime)
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
                    (p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) > currentSortDate
                    || ((p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) == currentSortDate && p.Id > current.Id)
                ))
            .OrderByOldest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        var nextIds = await query
            .Where(p => p.Id != current.Id
                && (
                    (p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) < currentSortDate
                    || ((p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) == currentSortDate && p.Id < current.Id)
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

    private async Task<PostDto?> LoadPostAsync(int id, CancellationToken cancellationToken)
    {
        var postsById = await LoadPostsByIdAsync([id], cancellationToken);
        return postsById.GetValueOrDefault(id);
    }

    private async Task<Dictionary<int, PostDto>> LoadPostsByIdAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var posts = await _context.Posts
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

        return posts.ToDictionary(p => p.Id, MapPost);
    }

    private static int NormalizeAroundWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundWindowSize);

    private static int NormalizeAroundIdWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundIdWindowSize);

    private sealed record AroundPostIds(IReadOnlyList<int> PrevIds, IReadOnlyList<int> NextIds);

    private static PostDto MapPost(Post post)
    {
        var representativeFile = GetRepresentativeFile(post);

        return new PostDto
        {
            Id = post.Id,
            LibraryId = representativeFile?.LibraryId ?? 0,
            LibraryName = representativeFile?.Library?.Name ?? string.Empty,
            RelativePath = representativeFile?.RelativePath ?? string.Empty,
            ContentHash = representativeFile?.ContentHash ?? string.Empty,
            SizeBytes = representativeFile?.SizeBytes ?? 0,
            Width = representativeFile?.Width ?? 0,
            Height = representativeFile?.Height ?? 0,
            ContentType = representativeFile?.ContentType ?? string.Empty,
            ImportDate = post.ImportDate,
            FileModifiedDate = representativeFile?.FileModifiedDate ?? default,
            IsFavorite = post.IsFavorite,
            ThumbnailLibraryId = representativeFile?.LibraryId ?? 0,
            ThumbnailContentHash = representativeFile?.ContentHash ?? string.Empty,
            Sources = post.Sources.OrderBy(s => s.Order).Select(s => s.Url).ToList(),
            PostFiles = post.PostFiles
                .OrderBy(pf => pf.Id)
                .Select(pf => new PostFileDto
                {
                    LibraryId = pf.LibraryId,
                    LibraryName = pf.Library?.Name,
                    RelativePath = pf.RelativePath,
                    ContentHash = pf.ContentHash,
                    SizeBytes = pf.SizeBytes,
                    Width = pf.Width,
                    Height = pf.Height,
                    ContentType = pf.ContentType,
                    FileModifiedDate = pf.FileModifiedDate,
                })
                .ToList(),
            Tags = post.PostTags
                .GroupBy(pt => new { pt.Tag.Id, pt.Tag.Name, pt.Tag.Category, pt.Tag.PostCount })
                .OrderBy(group => GetTagCategoryDisplayOrder(group.Key.Category))
                .ThenBy(group => group.Key.Name)
                .Select(group => new TagDto
                {
                    Id = group.Key.Id,
                    Name = group.Key.Name,
                    Category = group.Key.Category,
                    Usages = group.Key.PostCount,
                    Sources = group.Select(pt => pt.Source).Distinct().OrderBy(source => source).ToList(),
                })
                .ToList(),
            SimilarPosts = post.DuplicateGroupEntries
                .Select(dge => dge.DuplicateGroup)
                .SelectMany(g => g.Entries)
                .Where(e => e.PostId != post.Id)
                .Select(e =>
                {
                    var similarFile = GetRepresentativeFile(e.Post);
                    return new SimilarPostDto
                    {
                        Id = e.Post.Id,
                        LibraryId = similarFile?.LibraryId ?? 0,
                        LibraryName = similarFile?.Library?.Name ?? string.Empty,
                        RelativePath = similarFile?.RelativePath ?? string.Empty,
                        Width = similarFile?.Width ?? 0,
                        Height = similarFile?.Height ?? 0,
                        SizeBytes = similarFile?.SizeBytes ?? 0,
                        ContentType = similarFile?.ContentType ?? string.Empty,
                        ThumbnailLibraryId = similarFile?.LibraryId ?? 0,
                        ThumbnailContentHash = similarFile?.ContentHash ?? string.Empty,
                        DuplicateType = e.DuplicateGroup.Type,
                        SimilarityPercent = e.DuplicateGroup.SimilarityPercent,
                        GroupIsResolved = e.DuplicateGroup.IsResolved,
                    };
                })
                .ToList()
        };
    }

    private static int GetTagCategoryDisplayOrder(TagCategoryKind category)
        => category switch
        {
            TagCategoryKind.Artist => 0,
            TagCategoryKind.Character => 1,
            TagCategoryKind.Copyright => 2,
            TagCategoryKind.Meta => 3,
            _ => 4,
        };

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

    private static PostFile? GetRepresentativeFile(Post post)
        => post.PostFiles
            .OrderBy(pf => pf.Id)
            .FirstOrDefault();

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
