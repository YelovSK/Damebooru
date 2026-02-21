using Bakabooru.Core.Entities;
using Bakabooru.Core.DTOs;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Bakabooru.Processing.Extensions;
using Bakabooru.Processing.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class PostReadService
{
    private const int MaxPageSize = 500;

    private readonly BakabooruDbContext _context;

    public PostReadService(BakabooruDbContext context)
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
                LibraryId = p.LibraryId,
                RelativePath = p.RelativePath,
                ContentHash = p.ContentHash,
                SizeBytes = p.SizeBytes,
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.FileModifiedDate,
                IsFavorite = p.IsFavorite,
                Sources = new List<string>(),
                ThumbnailLibraryId = p.LibraryId,
                ThumbnailContentHash = p.ContentHash,
                ContentPostId = p.Id,
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

    public async Task<Result<PostsAroundDto>> GetPostsAroundAsync(int id, string? tags, CancellationToken cancellationToken)
    {
        var parsedQuery = QueryParser.Parse(tags ?? string.Empty);

        var current = await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Id, p.FileModifiedDate })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
        {
            return Result<PostsAroundDto>.Failure(OperationError.NotFound, "Post not found");
        }

        var currentSortDate = current.FileModifiedDate;

        var query = await ApplySearchFiltersAsync(_context.Posts.AsNoTracking(), parsedQuery, cancellationToken);

        var prevRaw = await query
            .Where(p => p.Id != current.Id
                && (
                    p.FileModifiedDate > currentSortDate
                    || (p.FileModifiedDate == currentSortDate && p.Id > current.Id)
                ))
            .OrderByOldest()
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var nextRaw = await query
            .Where(p => p.Id != current.Id
                && (
                    p.FileModifiedDate < currentSortDate
                    || (p.FileModifiedDate == currentSortDate && p.Id < current.Id)
                ))
            .OrderByNewest()
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var previous = prevRaw != null ? LoadPostAsync(prevRaw.Id, cancellationToken) : Task.FromResult<PostDto?>(null);
        var next = nextRaw != null ? LoadPostAsync(nextRaw.Id, cancellationToken) : Task.FromResult<PostDto?>(null);
        var tasks = await Task.WhenAll(previous, next);

        return Result<PostsAroundDto>.Success(new PostsAroundDto
        {
            Prev = tasks[0],
            Next = tasks[1]
        });
    }

    private async Task<PostDto?> LoadPostAsync(int id, CancellationToken cancellationToken)
    {
        var postEntity = await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id == id)
            .AsSplitQuery()
            .Select(p => new
            {
                Id = p.Id,
                LibraryId = p.LibraryId,
                LibraryName = p.Library.Name,
                RelativePath = p.RelativePath,
                ContentHash = p.ContentHash,
                SizeBytes = p.SizeBytes,
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.FileModifiedDate,
                IsFavorite = p.IsFavorite,
                Sources = p.Sources.OrderBy(s => s.Order).Select(s => s.Url).ToList(),
                TagLinks = p.PostTags
                    .OrderBy(pt => pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Order : int.MaxValue)
                    .ThenBy(pt => pt.Tag.Name)
                    .Select(pt => new
                    {
                        Id = pt.Tag.Id,
                        Name = pt.Tag.Name,
                        CategoryId = pt.Tag.TagCategoryId,
                        CategoryName = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Name : null,
                        CategoryColor = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Color : null,
                        Usages = pt.Tag.PostCount,
                        Source = pt.Source,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (postEntity == null)
        {
            return null;
        }

        var tags = postEntity.TagLinks
            .GroupBy(link => link.Id)
            .Select(group =>
            {
                var first = group.First();
                return new TagDto
                {
                    Id = first.Id,
                    Name = first.Name,
                    CategoryId = first.CategoryId,
                    CategoryName = first.CategoryName,
                    CategoryColor = first.CategoryColor,
                    Usages = first.Usages,
                    Sources = group.Select(link => link.Source).Distinct().OrderBy(source => source).ToList()
                };
            })
            .ToList();

        return new PostDto
        {
            Id = postEntity.Id,
            LibraryId = postEntity.LibraryId,
            LibraryName = postEntity.LibraryName,
            RelativePath = postEntity.RelativePath,
            ContentHash = postEntity.ContentHash,
            SizeBytes = postEntity.SizeBytes,
            Width = postEntity.Width,
            Height = postEntity.Height,
            ContentType = postEntity.ContentType,
            ImportDate = postEntity.ImportDate,
            FileModifiedDate = postEntity.FileModifiedDate,
            IsFavorite = postEntity.IsFavorite,
            Sources = postEntity.Sources,
            ThumbnailLibraryId = postEntity.LibraryId,
            ThumbnailContentHash = postEntity.ContentHash,
            ContentPostId = postEntity.Id,
            Tags = tags,
        };
    }

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
                (includeImage && p.ContentType.StartsWith("image/") && p.ContentType != "image/gif")
                || (includeAnimation && p.ContentType == "image/gif")
                || (includeVideo && p.ContentType.StartsWith("video/")));
        }

        if (parsedQuery.ExcludedMediaTypes.Count > 0)
        {
            if (parsedQuery.ExcludedMediaTypes.Contains(PostMediaType.Image))
            {
                query = query.Where(p => !(p.ContentType.StartsWith("image/") && p.ContentType != "image/gif"));
            }

            if (parsedQuery.ExcludedMediaTypes.Contains(PostMediaType.Animation))
            {
                query = query.Where(p => p.ContentType != "image/gif");
            }

            if (parsedQuery.ExcludedMediaTypes.Contains(PostMediaType.Video))
            {
                query = query.Where(p => !p.ContentType.StartsWith("video/"));
            }
        }

        if (parsedQuery.TagCountFilter != null)
        {
            var count = parsedQuery.TagCountFilter.Value;
            query = parsedQuery.TagCountFilter.Operator switch
            {
                NumericComparisonOperator.Equal => query.Where(p => p.PostTags.Count() == count),
                NumericComparisonOperator.GreaterThan => query.Where(p => p.PostTags.Count() > count),
                NumericComparisonOperator.GreaterThanOrEqual => query.Where(p => p.PostTags.Count() >= count),
                NumericComparisonOperator.LessThan => query.Where(p => p.PostTags.Count() < count),
                NumericComparisonOperator.LessThanOrEqual => query.Where(p => p.PostTags.Count() <= count),
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
            query = query.Where(p => EF.Functions.Like(p.RelativePath, pattern));
        }

        foreach (var filename in excludedFilenames)
        {
            var pattern = BuildFilenamePattern(filename);
            query = query.Where(p => !EF.Functions.Like(p.RelativePath, pattern));
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
