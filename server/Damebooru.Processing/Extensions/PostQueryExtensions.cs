using Damebooru.Core.Entities;
using Damebooru.Processing.Pipeline;

namespace Damebooru.Processing.Extensions;

public static class PostQueryExtensions
{
    public static IOrderedQueryable<Post> OrderByNewest(this IQueryable<Post> query) => query
            .OrderByDescending(p => p.FileModifiedDate)
            .ThenByDescending(p => p.Id);

    public static IOrderedQueryable<Post> OrderByOldest(this IQueryable<Post> query) => query
            .OrderBy(p => p.FileModifiedDate)
            .ThenBy(p => p.Id);

    public static IOrderedQueryable<Post> ApplySorting(this IQueryable<Post> query, SearchQuery searchQuery)
    {
        return (searchQuery.SortField, searchQuery.SortDirection) switch
        {
            (SearchSortField.FileModifiedDate, SearchSortDirection.Asc) => query
                .OrderBy(p => p.FileModifiedDate)
                .ThenBy(p => p.Id),
            (SearchSortField.FileModifiedDate, SearchSortDirection.Desc) => query
                .OrderByDescending(p => p.FileModifiedDate)
                .ThenByDescending(p => p.Id),

            (SearchSortField.ImportDate, SearchSortDirection.Asc) => query
                .OrderBy(p => p.ImportDate)
                .ThenBy(p => p.Id),
            (SearchSortField.ImportDate, SearchSortDirection.Desc) => query
                .OrderByDescending(p => p.ImportDate)
                .ThenByDescending(p => p.Id),

            (SearchSortField.TagCount, SearchSortDirection.Asc) => query
                .OrderBy(p => p.PostTags.Count())
                .ThenBy(p => p.Id),
            (SearchSortField.TagCount, SearchSortDirection.Desc) => query
                .OrderByDescending(p => p.PostTags.Count())
                .ThenByDescending(p => p.Id),

            (SearchSortField.Width, SearchSortDirection.Asc) => query
                .OrderBy(p => p.Width)
                .ThenBy(p => p.Id),
            (SearchSortField.Width, SearchSortDirection.Desc) => query
                .OrderByDescending(p => p.Width)
                .ThenByDescending(p => p.Id),

            (SearchSortField.Height, SearchSortDirection.Asc) => query
                .OrderBy(p => p.Height)
                .ThenBy(p => p.Id),
            (SearchSortField.Height, SearchSortDirection.Desc) => query
                .OrderByDescending(p => p.Height)
                .ThenByDescending(p => p.Id),

            (SearchSortField.SizeBytes, SearchSortDirection.Asc) => query
                .OrderBy(p => p.SizeBytes)
                .ThenBy(p => p.Id),
            (SearchSortField.SizeBytes, SearchSortDirection.Desc) => query
                .OrderByDescending(p => p.SizeBytes)
                .ThenByDescending(p => p.Id),

            (SearchSortField.Id, SearchSortDirection.Asc) => query.OrderBy(p => p.Id),
            (SearchSortField.Id, SearchSortDirection.Desc) => query.OrderByDescending(p => p.Id),

            _ => query.OrderByNewest()
        };
    }
}
