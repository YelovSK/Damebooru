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
            .Where(p => p.LibraryId == libraryId);

        var scopedQuery = ApplyFolderScope(baseQuery, normalizedPath, recursive);
        var totalCount = await scopedQuery.CountAsync(cancellationToken);

        var posts = await scopedQuery
            .OrderByDescending(p => p.FileModifiedDate)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = p.LibraryId,
                LibraryName = library.Name,
                RelativePath = p.RelativePath,
                ContentHash = p.ContentHash,
                SizeBytes = p.SizeBytes,
                Width = p.Width,
                Height = p.Height,
                ContentType = p.ContentType,
                ImportDate = p.ImportDate,
                FileModifiedDate = p.FileModifiedDate,
                IsFavorite = p.IsFavorite,
                ThumbnailLibraryId = p.LibraryId,
                ThumbnailContentHash = p.ContentHash,
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
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeFolderPath(path, out var normalizedPath))
        {
            return Result<PostsAroundDto>.Failure(OperationError.InvalidInput, "Invalid folder path.");
        }

        var scopedQuery = ApplyFolderScope(
            _context.Posts
                .AsNoTracking()
                .Where(p => p.LibraryId == libraryId),
            normalizedPath,
            recursive: false);

        var current = await scopedQuery
            .Where(p => p.Id == postId)
            .Select(p => new { p.Id, p.FileModifiedDate })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
        {
            return Result<PostsAroundDto>.Failure(OperationError.NotFound, "Post not found in folder scope.");
        }

        var prevRaw = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    p.FileModifiedDate > current.FileModifiedDate
                    || (p.FileModifiedDate == current.FileModifiedDate && p.Id > current.Id)
                ))
            .OrderByOldest()
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var nextRaw = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    p.FileModifiedDate < current.FileModifiedDate
                    || (p.FileModifiedDate == current.FileModifiedDate && p.Id < current.Id)
                ))
            .OrderByNewest()
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var previous = prevRaw != null
            ? LoadPostAsync(prevRaw.Id, cancellationToken)
            : Task.FromResult<PostDto?>(null);
        var next = nextRaw != null
            ? LoadPostAsync(nextRaw.Id, cancellationToken)
            : Task.FromResult<PostDto?>(null);
        var tasks = await Task.WhenAll(previous, next);

        return Result<PostsAroundDto>.Success(new PostsAroundDto
        {
            Prev = tasks[0],
            Next = tasks[1],
        });
    }

    private async Task<PostDto?> LoadPostAsync(int id, CancellationToken cancellationToken)
    {
        return await _context.Posts
            .AsNoTracking()
            .Where(p => p.Id == id)
            .AsSplitQuery()
            .Select(p => new PostDto
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
                ThumbnailLibraryId = p.LibraryId,
                ThumbnailContentHash = p.ContentHash,
                Sources = p.Sources.OrderBy(s => s.Order).Select(s => s.Url).ToList(),
                Tags = p.PostTags
                    .Select(pt => new TagDto
                    {
                        Id = pt.Tag.Id,
                        Name = pt.Tag.Name,
                        CategoryId = pt.Tag.TagCategoryId,
                        CategoryName = pt.Tag.TagCategory!.Name,
                        CategoryColor = pt.Tag.TagCategory!.Color,
                        Usages = pt.Tag.PostCount,
                        Source = pt.Source,
                    })
                    .ToList(),
                SimilarPosts = p.DuplicateGroupEntries
                    .Select(dge => dge.DuplicateGroup)
                    .SelectMany(g => g.Entries)
                    .Where(e => e.PostId != p.Id)
                    .Select(e => new SimilarPostDto
                    {
                        Id = e.Post.Id,
                        LibraryId = e.Post.LibraryId,
                        LibraryName = e.Post.Library.Name,
                        RelativePath = e.Post.RelativePath,
                        Width = e.Post.Width,
                        Height = e.Post.Height,
                        SizeBytes = e.Post.SizeBytes,
                        ContentType = e.Post.ContentType,
                        ThumbnailLibraryId = e.Post.LibraryId,
                        ThumbnailContentHash = e.Post.ContentHash,
                        DuplicateType = e.DuplicateGroup.Type,
                        SimilarityPercent = e.DuplicateGroup.SimilarityPercent,
                        GroupIsResolved = e.DuplicateGroup.IsResolved,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<List<LibraryFolderNodeDto>> GetFoldersInternalAsync(
        int libraryId,
        string currentPath,
        CancellationToken cancellationToken)
    {
        var baseQuery = _context.Posts
            .AsNoTracking()
            .Where(p => p.LibraryId == libraryId)
            .Select(p => p.RelativePath.Replace('\\', '/'));

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

            return query.Where(p => !p.RelativePath.Replace('\\', '/').Contains('/'));
        }

        var prefix = currentPath + "/";
        var scoped = query.Where(p => p.RelativePath.Replace('\\', '/').StartsWith(prefix));
        if (recursive)
        {
            return scoped;
        }

        return scoped.Where(p => !p.RelativePath.Replace('\\', '/').Substring(prefix.Length).Contains('/'));
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
