using Bakabooru.Core.Entities;
using Bakabooru.Core.DTOs;
using Bakabooru.Core.Paths;
using Bakabooru.Data;
using Bakabooru.Processing.Extensions;
using Bakabooru.Processing.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class PostReadService
{
    private readonly BakabooruDbContext _context;

    public PostReadService(BakabooruDbContext context)
    {
        _context = context;
    }

    public async Task<PostListDto> GetPostsAsync(string? tags, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

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
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                ThumbnailUrl = MediaPaths.GetThumbnailUrl(p.ContentHash),
                ContentUrl = MediaPaths.GetPostContentUrl(p.Id),
                Tags = new List<TagDto>(),
            })
            .ToListAsync(cancellationToken);

        return new PostListDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<PostDto?> GetPostAsync(int id, CancellationToken cancellationToken)
    {
        return LoadPostAsync(id, cancellationToken);
    }

    public async Task<PostsAroundDto?> GetPostsAroundAsync(int id, string? tags, CancellationToken cancellationToken)
    {
        var parsedQuery = QueryParser.Parse(tags ?? string.Empty);

        var current = await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Id, p.ImportDate })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
        {
            return null;
        }

        var query = await ApplySearchFiltersAsync(_context.Posts.AsNoTracking(), parsedQuery, cancellationToken);

        var useOldestOrdering = parsedQuery.SortField == SearchSortField.ImportDate
            && parsedQuery.SortDirection == SearchSortDirection.Asc;

        var prevRaw = useOldestOrdering
            ? await query
                .Where(p => p.Id != current.Id
                            && (p.ImportDate < current.ImportDate
                                || (p.ImportDate == current.ImportDate && p.Id < current.Id)))
                .OrderByDescending(p => p.ImportDate)
                .ThenByDescending(p => p.Id)
                .Select(p => new { p.Id })
                .FirstOrDefaultAsync(cancellationToken)
            : await query
                .Where(p => p.Id != current.Id
                            && (p.ImportDate > current.ImportDate
                                || (p.ImportDate == current.ImportDate && p.Id > current.Id)))
                .OrderBy(p => p.ImportDate)
                .ThenBy(p => p.Id)
                .Select(p => new { p.Id })
                .FirstOrDefaultAsync(cancellationToken);

        var nextRaw = useOldestOrdering
            ? await query
                .Where(p => p.Id != current.Id
                            && (p.ImportDate > current.ImportDate
                                || (p.ImportDate == current.ImportDate && p.Id > current.Id)))
                .OrderBy(p => p.ImportDate)
                .ThenBy(p => p.Id)
                .Select(p => new { p.Id })
                .FirstOrDefaultAsync(cancellationToken)
            : await query
                .Where(p => p.Id != current.Id
                            && (p.ImportDate < current.ImportDate
                                || (p.ImportDate == current.ImportDate && p.Id < current.Id)))
                .OrderByDescending(p => p.ImportDate)
                .ThenByDescending(p => p.Id)
                .Select(p => new { p.Id })
                .FirstOrDefaultAsync(cancellationToken);

        PostDto? prev = null;
        PostDto? next = null;

        if (prevRaw != null && nextRaw != null)
        {
            var prevTask = LoadPostAsync(prevRaw.Id, cancellationToken);
            var nextTask = LoadPostAsync(nextRaw.Id, cancellationToken);
            await Task.WhenAll(prevTask, nextTask);
            prev = prevTask.Result;
            next = nextTask.Result;
        }
        else if (prevRaw != null)
        {
            prev = await LoadPostAsync(prevRaw.Id, cancellationToken);
        }
        else if (nextRaw != null)
        {
            next = await LoadPostAsync(nextRaw.Id, cancellationToken);
        }

        return new PostsAroundDto
        {
            Prev = prev,
            Next = next
        };
    }

    private async Task<PostDto?> LoadPostAsync(int id, CancellationToken cancellationToken)
    {
        var postCore = await _context.Posts
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(post => new
            {
                Id = post.Id,
                LibraryId = post.LibraryId,
                RelativePath = post.RelativePath,
                ContentHash = post.ContentHash,
                Width = post.Width,
                Height = post.Height,
                ContentType = post.ContentType,
                ImportDate = post.ImportDate
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (postCore == null) return null;

        var tags = await _context.PostTags
            .AsNoTracking()
            .Where(pt => pt.PostId == id)
            .Select(pt => new TagDto
            {
                Id = pt.Tag.Id,
                Name = pt.Tag.Name,
                CategoryId = pt.Tag.TagCategoryId,
                CategoryName = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Name : null,
                CategoryColor = pt.Tag.TagCategory != null ? pt.Tag.TagCategory.Color : null,
                Usages = pt.Tag.PostCount,
            })
            .ToListAsync(cancellationToken);

        return new PostDto
        {
            Id = postCore.Id,
            LibraryId = postCore.LibraryId,
            RelativePath = postCore.RelativePath,
            ContentHash = postCore.ContentHash,
            Width = postCore.Width,
            Height = postCore.Height,
            ContentType = postCore.ContentType,
            ImportDate = postCore.ImportDate,
            ThumbnailUrl = MediaPaths.GetThumbnailUrl(postCore.ContentHash),
            ContentUrl = MediaPaths.GetPostContentUrl(postCore.Id),
            Tags = tags
        };
    }

    private async Task<IQueryable<Post>> ApplySearchFiltersAsync(IQueryable<Post> query, SearchQuery parsedQuery, CancellationToken cancellationToken)
    {
        var includedTags = parsedQuery.IncludedTags.Distinct().ToList();
        var excludedTags = parsedQuery.ExcludedTags.Distinct().ToList();
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

        return query;
    }
}
