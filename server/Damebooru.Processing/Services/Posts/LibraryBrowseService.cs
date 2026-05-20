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

        var baseQuery = _context.Posts
            .AsNoTracking()
            .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId));

        var scopedQuery = ApplyFolderScope(baseQuery, normalizedPath, recursive);
        var totalCount = await scopedQuery.CountAsync(cancellationToken);

        var posts = await scopedQuery
            .Select(p => new
            {
                Post = p,
                p.Id,
                p.ImportDate,
                p.IsFavorite,
                p.PrimaryFileModifiedDate,
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
                LibraryFile = p.PostFiles
                    .Where(pf => pf.LibraryId == libraryId)
                    .OrderBy(pf => pf.Id)
                    .Select(pf => new
                    {
                        pf.LibraryId,
                        pf.RelativePath,
                    })
                    .FirstOrDefault(),
            })
            .OrderByDescending(p => p.PrimaryFileModifiedDate)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = p.LibraryFile == null ? 0 : p.LibraryFile.LibraryId,
                LibraryName = library.Name,
                RelativePath = p.LibraryFile == null
                    ? p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.RelativePath
                    : p.LibraryFile.RelativePath,
                ContentHash = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.ContentHash,
                SizeBytes = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.SizeBytes,
                Width = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.Width,
                Height = p.RepresentativeFile == null ? 0 : p.RepresentativeFile.Height,
                ContentType = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.ContentType,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.RepresentativeFile == null ? default : p.RepresentativeFile.FileModifiedDate,
                IsFavorite = p.IsFavorite,
                PostFiles = p.Post.PostFiles
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
                ThumbnailLibraryId = p.LibraryFile == null ? 0 : p.LibraryFile.LibraryId,
                ThumbnailContentHash = p.RepresentativeFile == null ? string.Empty : p.RepresentativeFile.ContentHash,
                Sources = new List<string>(),
                Tags = new List<TagDto>(),
                SimilarPosts = new List<SimilarPostDto>(),
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
        int window,
        int? before,
        int? after,
        CancellationToken cancellationToken = default)
    {
        var beforeSize = NormalizeAroundWindowSize(before ?? window);
        var afterSize = NormalizeAroundWindowSize(after ?? window);
        var idWindowSize = Math.Min(Math.Max(beforeSize, afterSize) + 1, MaxAroundIdWindowSize);
        var idsResult = await GetPostIdsAroundAsync(libraryId, postId, path, idWindowSize, cancellationToken);
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
        int libraryId,
        int postId,
        string? path,
        int window,
        CancellationToken cancellationToken = default)
    {
        var windowSize = NormalizeAroundIdWindowSize(window);
        if (!TryNormalizeFolderPath(path, out var normalizedPath))
        {
            return Result<AroundPostIds>.Failure(OperationError.InvalidInput, "Invalid folder path.");
        }

        var scopedQuery = ApplyFolderScope(
            _context.Posts
                .AsNoTracking()
                .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId)),
            normalizedPath,
            recursive: false);

        var current = await scopedQuery
            .Where(p => p.Id == postId)
            .Select(p => new
            {
                p.Id,
                FileModifiedDate = p.PrimaryFileModifiedDate ?? default(DateTime)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
        {
            return Result<AroundPostIds>.Failure(OperationError.NotFound, "Post not found in folder scope.");
        }

        var prevIds = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    (p.PrimaryFileModifiedDate ?? default(DateTime)) > current.FileModifiedDate
                    || ((p.PrimaryFileModifiedDate ?? default(DateTime)) == current.FileModifiedDate && p.Id > current.Id)
                ))
            .OrderByOldest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        var nextIds = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    (p.PrimaryFileModifiedDate ?? default(DateTime)) < current.FileModifiedDate
                    || ((p.PrimaryFileModifiedDate ?? default(DateTime)) == current.FileModifiedDate && p.Id < current.Id)
                ))
            .OrderByNewest()
            .Select(p => p.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        return Result<AroundPostIds>.Success(new AroundPostIds(prevIds, nextIds));
    }

    private static int NormalizeAroundWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundWindowSize);

    private static int NormalizeAroundIdWindowSize(int window)
        => Math.Clamp(window, 1, MaxAroundIdWindowSize);

    private sealed record AroundPostIds(IReadOnlyList<int> PrevIds, IReadOnlyList<int> NextIds);

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
                .Select(pf => pf.RelativePath.Replace('\\', '/')));

        if (!string.IsNullOrEmpty(currentPath))
        {
            var prefix = currentPath + "/";
            baseQuery = baseQuery.Where(p => p.StartsWith(prefix));
        }

        var relativePaths = await baseQuery.ToListAsync(cancellationToken);
        return BuildChildFolders(relativePaths, currentPath);
    }

    private static IQueryable<Core.Entities.Post> ApplyFolderScope(
        IQueryable<Core.Entities.Post> query,
        string currentPath,
        bool recursive)
    {
        if (string.IsNullOrEmpty(currentPath))
        {
            if (recursive)
            {
                return query;
            }

            return query.Where(p => p.PostFiles.Any(pf => !pf.RelativePath.Replace('\\', '/').Contains('/')));
        }

        var prefix = currentPath + "/";
        var scoped = query.Where(p => p.PostFiles.Any(pf => pf.RelativePath.Replace('\\', '/').StartsWith(prefix)));
        if (recursive)
        {
            return scoped;
        }

        return scoped.Where(p => p.PostFiles.Any(pf =>
            pf.RelativePath.Replace('\\', '/').StartsWith(prefix)
            && !pf.RelativePath.Replace('\\', '/').Substring(prefix.Length).Contains('/')));
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
        IEnumerable<string> normalizedRelativePaths,
        string currentPath)
    {
        var folders = new Dictionary<string, LibraryFolderNodeDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePathRaw in normalizedRelativePaths)
        {
            var relativePath = NormalizeStoredPath(relativePathRaw);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

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

    private static string NormalizeStoredPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        return normalized.Trim('/');
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
