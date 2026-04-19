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
            .OrderByDescending(p => p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault())
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PostDto
            {
                Id = p.Id,
                LibraryId = p.PostFiles
                    .Where(pf => pf.LibraryId == libraryId)
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.LibraryId)
                    .FirstOrDefault(),
                LibraryName = library.Name,
                RelativePath = p.PostFiles
                    .Where(pf => pf.LibraryId == libraryId)
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.RelativePath)
                    .FirstOrDefault() ?? p.PostFiles.OrderBy(pf => pf.Id).Select(pf => pf.RelativePath).FirstOrDefault() ?? string.Empty,
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
                    .Where(pf => pf.LibraryId == libraryId)
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.LibraryId)
                    .FirstOrDefault(),
                ThumbnailContentHash = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => pf.ContentHash)
                    .FirstOrDefault() ?? string.Empty,
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
                .Where(p => p.PostFiles.Any(pf => pf.LibraryId == libraryId)),
            normalizedPath,
            recursive: false);

        var current = await scopedQuery
            .Where(p => p.Id == postId)
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
            return Result<PostsAroundDto>.Failure(OperationError.NotFound, "Post not found in folder scope.");
        }

        var prevRaw = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    (p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) > current.FileModifiedDate
                    || ((p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) == current.FileModifiedDate && p.Id > current.Id)
                ))
            .OrderByOldest()
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var nextRaw = await scopedQuery
            .Where(p => p.Id != current.Id
                && (
                    (p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) < current.FileModifiedDate
                    || ((p.PostFiles.OrderBy(pf => pf.Id).Select(pf => (DateTime?)pf.FileModifiedDate).FirstOrDefault() ?? default(DateTime)) == current.FileModifiedDate && p.Id < current.Id)
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
        var post = await _context.Posts
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
            .Where(p => p.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

        return post == null ? null : MapPost(post);
    }

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
                .ToList(),
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

    private static PostFile? GetRepresentativeFile(Post post)
        => post.PostFiles
            .OrderBy(pf => pf.Id)
            .FirstOrDefault();

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
