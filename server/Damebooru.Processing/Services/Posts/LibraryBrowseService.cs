using Damebooru.Core.Entities;
using Damebooru.Core.DTOs;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class LibraryBrowseService
{
    private const int DefaultPageSize = 80;
    private const int MaxPageSize = 200;
    private const int MaxAroundWindowSize = 100;
    private const int MaxAroundIdWindowSize = 100;

    private readonly DamebooruDbContext _context;

    public LibraryBrowseService(DamebooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result<LibraryBrowseResponseDto>> BrowseAsync(
        int libraryId,
        string? path,
        bool recursive,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var library = await _context.Libraries
            .AsNoTracking()
            .Where(l => l.Id == libraryId)
            .Select(l => new { l.Id, l.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (library == null)
        {
            return Result<LibraryBrowseResponseDto>.Failure(OperationError.NotFound, "Library not found.");
        }

        if (!TryNormalizeFolderPath(path, out var normalizedPath))
        {
            return Result<LibraryBrowseResponseDto>.Failure(OperationError.InvalidInput, "Invalid folder path.");
        }

        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1)
        {
            pageSize = DefaultPageSize;
        }

        if (pageSize > MaxPageSize)
        {
            pageSize = MaxPageSize;
        }

        var scopedQuery = ApplyFolderScope(_context.Posts.AsNoTracking(), libraryId, normalizedPath, recursive);
        var totalCount = await scopedQuery.CountAsync(cancellationToken);

        var posts = await scopedQuery
            .OrderByDescending(p => p.FileModifiedDate)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = libraryId,
                LibraryName = library.Name,
                // Show the copy that lives in the browsed library.
                RelativePath = p.PostFiles
                    .Where(pf => pf.LibraryId == libraryId)
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.RelativePath)
                    .FirstOrDefault() ?? string.Empty,
                ContentHash = p.ContentHash,
                SizeBytes = p.SizeBytes,
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.FileModifiedDate,
                IsFavorite = p.IsFavorite,
                PostFiles = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => new PostFileDto
                    {
                        LibraryId = pf.LibraryId,
                        RelativePath = pf.RelativePath,
                        FileModifiedDate = pf.FileModifiedDate,
                    })
                    .ToList(),
                Sources = new List<string>(),
                Tags = new List<TagDto>(),
            })
            .ToListAsync(cancellationToken);

        var childFolders = await GetFoldersInternalAsync(libraryId, normalizedPath, cancellationToken);

        return Result<LibraryBrowseResponseDto>.Success(new LibraryBrowseResponseDto
        {
            LibraryId = library.Id,
            LibraryName = library.Name,
            CurrentPath = normalizedPath,
            Recursive = recursive,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Breadcrumbs = BuildBreadcrumbs(library.Name, normalizedPath),
            ChildFolders = childFolders,
            Posts = posts,
        });
    }

    public async Task<Result<List<LibraryFolderNodeDto>>> GetFoldersAsync(
        int libraryId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var libraryExists = await _context.Libraries
            .AsNoTracking()
            .AnyAsync(l => l.Id == libraryId, cancellationToken);

        if (!libraryExists)
        {
            return Result<List<LibraryFolderNodeDto>>.Failure(OperationError.NotFound, "Library not found.");
        }

        if (!TryNormalizeFolderPath(path, out var normalizedPath))
        {
            return Result<List<LibraryFolderNodeDto>>.Failure(OperationError.InvalidInput, "Invalid folder path.");
        }

        var folders = await GetFoldersInternalAsync(libraryId, normalizedPath, cancellationToken);
        return Result<List<LibraryFolderNodeDto>>.Success(folders);
    }

    public async Task<Result<PostsAroundDto>> GetPostsAroundAsync(
        int libraryId,
        int postId,
        string? path,
        FolderPostOrder order,
        int window,
        int? before,
        int? after,
        CancellationToken cancellationToken = default)
    {
        var beforeSize = NormalizeAroundWindowSize(before ?? window);
        var afterSize = NormalizeAroundWindowSize(after ?? window);
        var idWindowSize = Math.Min(Math.Max(beforeSize, afterSize) + 1, MaxAroundIdWindowSize);
        var idsResult = await GetPostIdsAroundAsync(libraryId, postId, path, order, idWindowSize, cancellationToken);
        if (!idsResult.IsSuccess)
        {
            return Result<PostsAroundDto>.Failure(
                idsResult.Error ?? OperationError.InvalidInput,
                idsResult.Message ?? "Failed to load surrounding posts.");
        }

        var ids = idsResult.Value!;
        var prevIds = ids.PrevIds.Take(beforeSize).ToList();
        var nextIds = ids.NextIds.Take(afterSize).ToList();
        var postsById = await PostDtoLoader.LoadPostsByIdAsync(_context, prevIds.Concat([postId]).Concat(nextIds), cancellationToken);
        if (!postsById.TryGetValue(postId, out var currentPost))
        {
            return Result<PostsAroundDto>.Failure(OperationError.NotFound, "Post not found in folder scope.");
        }

        var prevItems = prevIds
            .Where(postsById.ContainsKey)
            .Select(id => postsById[id])
            .ToList();
        var nextItems = nextIds
            .Where(postsById.ContainsKey)
            .Select(id => postsById[id])
            .ToList();
        var orderedItems = prevItems
            .AsEnumerable()
            .Reverse()
            .Concat([currentPost])
            .Concat(nextItems)
            .ToList();

        return Result<PostsAroundDto>.Success(new PostsAroundDto
        {
            Items = orderedItems,
            HasPrevious = ids.PrevIds.Count > beforeSize,
            HasNext = ids.NextIds.Count > afterSize,
            Offset = ids.Position - prevItems.Count,
            TotalCount = ids.TotalCount,
        });
    }

    private async Task<Result<AroundPostIds>> GetPostIdsAroundAsync(
        int libraryId,
        int postId,
        string? path,
        FolderPostOrder order,
        int window,
        CancellationToken cancellationToken = default)
    {
        var windowSize = NormalizeAroundIdWindowSize(window);
        if (!TryNormalizeFolderPath(path, out var normalizedPath))
        {
            return Result<AroundPostIds>.Failure(OperationError.InvalidInput, "Invalid folder path.");
        }

        var scopedQuery = ApplyFolderScope(_context.Posts.AsNoTracking(), libraryId, normalizedPath, recursive: false);
        var ids = order == FolderPostOrder.Name
            ? await GetNameOrderedIdsAroundAsync(scopedQuery, libraryId, normalizedPath, postId, windowSize, cancellationToken)
            : await GetDateOrderedIdsAroundAsync(scopedQuery, postId, windowSize, cancellationToken);

        return ids == null
            ? Result<AroundPostIds>.Failure(OperationError.NotFound, "Post not found in folder scope.")
            : Result<AroundPostIds>.Success(ids);
    }

    private static async Task<AroundPostIds?> GetDateOrderedIdsAroundAsync(
        IQueryable<Post> scopedQuery,
        int postId,
        int windowSize,
        CancellationToken cancellationToken)
    {
        var current = await scopedQuery
            .Where(p => p.Id == postId)
            .Select(p => new
            {
                p.Id,
                p.FileModifiedDate
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
        {
            return null;
        }

        var prevQuery = scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    p.FileModifiedDate > current.FileModifiedDate
                    || (p.FileModifiedDate == current.FileModifiedDate && p.Id > current.Id)
                ));

        var prevIds = await prevQuery
            .OrderByOldest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        var nextIds = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    p.FileModifiedDate < current.FileModifiedDate
                    || (p.FileModifiedDate == current.FileModifiedDate && p.Id < current.Id)
                ))
            .OrderByNewest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        var position = await prevQuery.CountAsync(cancellationToken);
        var totalCount = await scopedQuery.CountAsync(cancellationToken);
        return new AroundPostIds(prevIds, nextIds, position, totalCount);
    }

    /// <summary>Sorts the whole folder in memory: natural order can't be expressed in SQL, and a single folder is small.</summary>
    private static async Task<AroundPostIds?> GetNameOrderedIdsAroundAsync(
        IQueryable<Post> scopedQuery,
        int libraryId,
        string folderPath,
        int postId,
        int windowSize,
        CancellationToken cancellationToken)
    {
        var posts = await scopedQuery
            .Select(p => new
            {
                p.Id,
                Paths = p.PostFiles
                    .Where(pf => pf.LibraryId == libraryId)
                    .Select(pf => pf.RelativePath)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        var orderedIds = posts
            .Select(p => new
            {
                p.Id,
                Name = p.Paths
                    .Select(path => GetDirectChildName(path, folderPath))
                    .OfType<string>()
                    .Min(NaturalStringComparer.Instance),
            })
            .OrderBy(p => p.Name, NaturalStringComparer.Instance)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .ToList();

        var position = orderedIds.IndexOf(postId);
        if (position < 0)
        {
            return null;
        }

        var prevStart = Math.Max(0, position - windowSize);
        var prevIds = orderedIds.GetRange(prevStart, position - prevStart);
        prevIds.Reverse();
        var nextIds = orderedIds.GetRange(position + 1, Math.Min(windowSize, orderedIds.Count - position - 1));
        return new AroundPostIds(prevIds, nextIds, position, orderedIds.Count);
    }

    private static string? GetDirectChildName(string relativePath, string folderPath)
    {
        var name = string.IsNullOrEmpty(folderPath)
            ? relativePath
            : relativePath.StartsWith(folderPath + "/", StringComparison.Ordinal)
                ? relativePath[(folderPath.Length + 1)..]
                : null;

        return name == null || name.Contains('/') ? null : name;
    }

    private static int NormalizeAroundWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundWindowSize);

    private static int NormalizeAroundIdWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundIdWindowSize);

    /// <param name="Position">Index of the anchor post in the whole scope.</param>
    private sealed record AroundPostIds(IReadOnlyList<int> PrevIds, IReadOnlyList<int> NextIds, int Position, int TotalCount);

    private async Task<List<LibraryFolderNodeDto>> GetFoldersInternalAsync(
        int libraryId,
        string currentPath,
        CancellationToken cancellationToken)
    {
        var baseQuery = _context.Posts
            .AsNoTracking()
            .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId))
            .SelectMany(p => p.PostFiles
                .Where(pf => pf.LibraryId == libraryId)
                .Select(pf => pf.RelativePath));

        if (!string.IsNullOrEmpty(currentPath))
        {
            var prefix = currentPath + "/";
            baseQuery = baseQuery.Where(p => p.StartsWith(prefix));
        }

        var relativePaths = await baseQuery.ToListAsync(cancellationToken);
        return BuildChildFolders(relativePaths, currentPath);
    }

    private static IQueryable<Post> ApplyFolderScope(
        IQueryable<Post> query,
        int libraryId,
        string currentPath,
        bool recursive)
    {
        if (string.IsNullOrEmpty(currentPath))
        {
            return recursive
                ? query.Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId))
                : query.Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId && !pf.RelativePath.Contains('/')));
        }

        var prefix = currentPath + "/";
        return recursive
            ? query.Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId && pf.RelativePath.StartsWith(prefix)))
            : query.Where(p => p.PostFiles.Any(pf =>
                pf.LibraryId == libraryId
                && pf.RelativePath.StartsWith(prefix)
                && !pf.RelativePath.Substring(prefix.Length).Contains('/')));
    }

    private static List<LibraryBrowseBreadcrumbDto> BuildBreadcrumbs(string libraryName, string currentPath)
    {
        var breadcrumbs = new List<LibraryBrowseBreadcrumbDto>
        {
            new()
            {
                Name = libraryName,
                Path = string.Empty,
            },
        };

        if (string.IsNullOrEmpty(currentPath))
        {
            return breadcrumbs;
        }

        var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segmentPath = string.Empty;
        foreach (var segment in segments)
        {
            segmentPath = string.IsNullOrEmpty(segmentPath)
                ? segment
                : $"{segmentPath}/{segment}";

            breadcrumbs.Add(new LibraryBrowseBreadcrumbDto
            {
                Name = segment,
                Path = segmentPath,
            });
        }

        return breadcrumbs;
    }

    private static List<LibraryFolderNodeDto> BuildChildFolders(
        IEnumerable<string> relativePaths,
        string currentPath)
    {
        var folders = new Dictionary<string, LibraryFolderNodeDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in relativePaths)
        {
            var remainder = GetPathRemainder(relativePath, currentPath);
            if (string.IsNullOrEmpty(remainder))
            {
                continue;
            }

            var childSeparatorIndex = remainder.IndexOf('/');
            if (childSeparatorIndex < 0)
            {
                continue;
            }

            var childName = remainder[..childSeparatorIndex];
            if (string.IsNullOrEmpty(childName))
            {
                continue;
            }

            var childPath = string.IsNullOrEmpty(currentPath)
                ? childName
                : $"{currentPath}/{childName}";

            if (!folders.TryGetValue(childName, out var child))
            {
                child = new LibraryFolderNodeDto
                {
                    Name = childName,
                    Path = childPath,
                };

                folders[childName] = child;
            }

            child.RecursivePostCount += 1;

            var afterChild = remainder[(childSeparatorIndex + 1)..];
            if (string.IsNullOrEmpty(afterChild))
            {
                continue;
            }

            if (afterChild.Contains('/'))
            {
                child.HasChildren = true;
            }
        }

        return folders.Values
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetPathRemainder(string relativePath, string currentPath)
    {
        if (string.IsNullOrEmpty(currentPath))
        {
            return relativePath;
        }

        var prefix = currentPath + "/";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return relativePath[prefix.Length..];
    }

    private static bool TryNormalizeFolderPath(string? path, out string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            normalizedPath = string.Empty;
            return true;
        }

        var parts = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                normalizedPath = string.Empty;
                return false;
            }
        }

        normalizedPath = string.Join('/', parts);
        return true;
    }
}
