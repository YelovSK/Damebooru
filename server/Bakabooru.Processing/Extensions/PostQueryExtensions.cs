using Bakabooru.Core.Entities;
using Bakabooru.Processing.Pipeline;

namespace Bakabooru.Processing.Extensions;

public static class PostQueryExtensions
{
    public static IOrderedQueryable<Post> OrderByNewest(this IQueryable<Post> query)
    {
        return query
            .OrderByDescending(p => p.ImportDate)
            .ThenByDescending(p => p.Id);
    }

    public static IOrderedQueryable<Post> ApplySorting(this IQueryable<Post> query, SearchQuery searchQuery)
    {
        return (searchQuery.SortField, searchQuery.SortDirection) switch
        {
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
