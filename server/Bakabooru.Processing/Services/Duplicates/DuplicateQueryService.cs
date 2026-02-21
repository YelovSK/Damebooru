using Bakabooru.Core.DTOs;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class DuplicateQueryService
{
    private readonly BakabooruDbContext _context;

    public DuplicateQueryService(BakabooruDbContext context)
    {
        _context = context;
    }

    public Task<List<DuplicateGroupDto>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        return _context.DuplicateGroups
            .Where(g => !g.IsResolved && g.Entries.Count >= 2)
            .OrderByDescending(g => g.SimilarityPercent ?? 100)
            .ThenByDescending(g => g.DetectedDate)
            .Select(g => new DuplicateGroupDto
            {
                Id = g.Id,
                Type = g.Type,
                SimilarityPercent = g.SimilarityPercent,
                DetectedDate = g.DetectedDate,
                Posts = g.Entries.Select(e => new DuplicatePostDto
                {
                    Id = e.Post.Id,
                    LibraryId = e.Post.LibraryId,
                    RelativePath = e.Post.RelativePath,
                    ContentHash = e.Post.ContentHash,
                    Width = e.Post.Width,
                    Height = e.Post.Height,
                    ContentType = e.Post.ContentType,
                    SizeBytes = e.Post.SizeBytes,
                    ImportDate = e.Post.ImportDate,
                    FileModifiedDate = e.Post.FileModifiedDate,
                    ThumbnailLibraryId = e.Post.LibraryId,
                    ThumbnailContentHash = e.Post.ContentHash,
                }).ToList()
            }).ToListAsync(cancellationToken);
    }

    public Task<List<DuplicateGroupDto>> GetResolvedDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        return _context.DuplicateGroups
            .Where(g => g.IsResolved)
            .OrderByDescending(g => g.DetectedDate)
            .Select(g => new DuplicateGroupDto
            {
                Id = g.Id,
                Type = g.Type,
                SimilarityPercent = g.SimilarityPercent,
                DetectedDate = g.DetectedDate,
                Posts = g.Entries.Select(e => new DuplicatePostDto
                {
                    Id = e.Post.Id,
                    LibraryId = e.Post.LibraryId,
                    RelativePath = e.Post.RelativePath,
                    ContentHash = e.Post.ContentHash,
                    Width = e.Post.Width,
                    Height = e.Post.Height,
                    ContentType = e.Post.ContentType,
                    SizeBytes = e.Post.SizeBytes,
                    ImportDate = e.Post.ImportDate,
                    FileModifiedDate = e.Post.FileModifiedDate,
                    ThumbnailLibraryId = e.Post.LibraryId,
                    ThumbnailContentHash = e.Post.ContentHash,
                }).ToList()
            }).ToListAsync(cancellationToken);
    }

    public async Task<List<SameFolderDuplicateGroupDto>> GetSameFolderDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _context.DuplicateGroups
            .AsNoTracking()
            .Where(g => !g.IsResolved)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.Library)
            .ToListAsync(cancellationToken);

        var result = new List<SameFolderDuplicateGroupDto>();

        foreach (var group in groups)
        {
            var sameFolderPartitions = group.Entries
                .Select(e => e.Post)
                .GroupBy(
                    p => new { p.LibraryId, FolderPath = GetParentFolderPath(p.RelativePath) },
                    p => p);

            foreach (var partition in sameFolderPartitions)
            {
                var posts = partition.ToList();
                if (posts.Count < 2)
                {
                    continue;
                }

                var orderedPosts = posts
                    .OrderByDescending(p => (long)p.Width * p.Height)
                    .ThenByDescending(p => p.SizeBytes)
                    .ThenByDescending(p => p.FileModifiedDate)
                    .ThenByDescending(p => p.Id)
                    .ToList();

                result.Add(new SameFolderDuplicateGroupDto
                {
                    ParentDuplicateGroupId = group.Id,
                    DuplicateType = group.Type,
                    SimilarityPercent = group.SimilarityPercent,
                    LibraryId = partition.Key.LibraryId,
                    LibraryName = orderedPosts[0].Library.Name,
                    FolderPath = partition.Key.FolderPath,
                    RecommendedKeepPostId = orderedPosts[0].Id,
                    Posts = posts
                        .OrderByDescending(p => (long)p.Width * p.Height)
                        .ThenByDescending(p => p.SizeBytes)
                        .ThenByDescending(p => p.FileModifiedDate)
                        .ThenByDescending(p => p.Id)
                        .Select(p => new SameFolderDuplicatePostDto
                        {
                            Id = p.Id,
                            LibraryId = p.LibraryId,
                            RelativePath = p.RelativePath,
                            ContentHash = p.ContentHash,
                            Width = p.Width,
                            Height = p.Height,
                            SizeBytes = p.SizeBytes,
                            ImportDate = p.ImportDate,
                            FileModifiedDate = p.FileModifiedDate,
                            ThumbnailLibraryId = p.LibraryId,
                            ThumbnailContentHash = p.ContentHash,
                        })
                        .ToList()
                });
            }
        }

        return result
            .OrderBy(r => r.DuplicateType == "exact" ? 0 : 1)
            .ThenByDescending(r => r.SimilarityPercent ?? 0)
            .ThenBy(r => r.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ParentDuplicateGroupId)
            .ToList();
    }

    public async Task<List<ExcludedFileDto>> GetExcludedFilesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ExcludedFiles
            .AsNoTracking()
            .Include(e => e.Library)
            .OrderByDescending(e => e.ExcludedDate)
            .Select(e => new ExcludedFileDto
            {
                Id = e.Id,
                LibraryId = e.LibraryId,
                LibraryName = e.Library.Name,
                RelativePath = e.RelativePath,
                ContentHash = e.ContentHash,
                ExcludedDate = e.ExcludedDate,
                Reason = e.Reason,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<string>> GetExcludedFileContentPathAsync(int excludedFileId, CancellationToken cancellationToken = default)
    {
        var entry = await _context.ExcludedFiles
            .AsNoTracking()
            .Where(e => e.Id == excludedFileId)
            .Select(e => new
            {
                e.RelativePath,
                LibraryPath = e.Library.Path,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (entry == null)
        {
            return Result<string>.Failure(OperationError.NotFound, "Excluded file not found.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(entry.LibraryPath, entry.RelativePath));
        var libraryRoot = Path.GetFullPath(entry.LibraryPath + Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Failure(OperationError.InvalidInput, "Invalid file path.");
        }

        if (!File.Exists(fullPath))
        {
            return Result<string>.Failure(OperationError.NotFound, "File not found on disk.");
        }

        return Result<string>.Success(fullPath);
    }

    private static string GetParentFolderPath(string relativePath)
    {
        var normalizedPath = NormalizePath(relativePath);
        var slashIndex = normalizedPath.LastIndexOf('/');
        return slashIndex < 0 ? string.Empty : normalizedPath[..slashIndex];
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        normalized = normalized.Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }
}
