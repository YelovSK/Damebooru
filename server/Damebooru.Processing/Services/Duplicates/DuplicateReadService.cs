using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Paths;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Duplicates;

public class DuplicateReadService
{
    private readonly DamebooruDbContext _context;

    public DuplicateReadService(DamebooruDbContext context)
    {
        _context = context;
    }

    public Task<List<DuplicateGroupDto>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        return GetDuplicateGroupsCoreAsync(resolved: false, cancellationToken);
    }

    public Task<List<DuplicateGroupDto>> GetResolvedDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        return GetDuplicateGroupsCoreAsync(resolved: true, cancellationToken);
    }

    private async Task<List<DuplicateGroupDto>> GetDuplicateGroupsCoreAsync(bool resolved, CancellationToken cancellationToken)
    {
        var groups = await _context.DuplicateGroups
            .AsNoTracking()
            .Where(g => g.IsResolved == resolved)
            .Where(g => g.Type != DuplicateType.Exact)
            .Include(g => g.Entries)
                .ThenInclude(e => e.Post)
                    .ThenInclude(p => p.PostFiles)
                        .ThenInclude(pf => pf.Library)
            .OrderByDescending(g => g.DetectedDate)
            .ToListAsync(cancellationToken);

        return groups
            .Where(g => g.Entries.Count >= 2)
            .Select(g =>
            {
                var posts = g.Entries.Select(e => e.Post).ToList();
                return new DuplicateGroupDto
                {
                    Id = g.Id,
                    Type = g.Type,
                    SimilarityPercent = g.SimilarityPercent,
                    DetectedDate = g.DetectedDate,
                    Posts = posts.Select(p => new DuplicatePostDto
                    {
                        Id = p.Id,
                        LibraryId = GetRepresentativeLibraryId(p),
                        LibraryName = GetRepresentativeLibraryName(p),
                        RelativePath = GetRepresentativeRelativePath(p),
                        ContentHash = GetRepresentativeContentHash(p),
                        Width = GetRepresentativeWidth(p),
                        Height = GetRepresentativeHeight(p),
                        ContentType = GetRepresentativeContentType(p),
                        SizeBytes = GetRepresentativeSizeBytes(p),
                        ImportDate = p.ImportDate,
                        FileModifiedDate = GetRepresentativeFileModifiedDate(p),
                        ThumbnailLibraryId = GetRepresentativeLibraryId(p),
                        ThumbnailContentHash = GetRepresentativeContentHash(p),
                        Files = GetPostFileDtos(p),
                    }).ToList()
                };
            })
            .OrderByDescending(g => g.SimilarityPercent ?? 100)
            .ThenByDescending(g => g.DetectedDate)
            .ToList();
    }

    public async Task<List<ExactDuplicateClusterDto>> GetExactDuplicateClustersAsync(CancellationToken cancellationToken = default)
    {
        var files = await _context.PostFiles
            .AsNoTracking()
            .Where(pf => pf.ContentHash != string.Empty)
            .Select(pf => new ExactDuplicateFileDto
            {
                PostId = pf.PostId,
                PostFileId = pf.Id,
                LibraryId = pf.LibraryId,
                LibraryName = pf.Library.Name,
                RelativePath = pf.RelativePath,
                ContentHash = pf.ContentHash,
                Width = pf.Width,
                Height = pf.Height,
                ContentType = pf.ContentType,
                SizeBytes = pf.SizeBytes,
                FileModifiedDate = pf.FileModifiedDate,
                ThumbnailLibraryId = pf.LibraryId,
                ThumbnailContentHash = pf.ContentHash,
            })
            .ToListAsync(cancellationToken);

        return files
            .GroupBy(file => file.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var folderBuckets = group
                    .GroupBy(file => new
                    {
                        file.LibraryId,
                        file.LibraryName,
                        FolderPath = DuplicatePathHelper.GetParentFolderPath(file.RelativePath)
                    })
                    .Select(folderGroup => new ExactDuplicateFolderBucketDto
                    {
                        LibraryId = folderGroup.Key.LibraryId,
                        LibraryName = folderGroup.Key.LibraryName,
                        FolderPath = folderGroup.Key.FolderPath,
                        Files = folderGroup
                            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(file => file.PostFileId)
                            .ToList()
                    })
                    .OrderBy(bucket => bucket.LibraryName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(bucket => bucket.FolderPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ExactDuplicateClusterDto
                {
                    ContentHash = group.Key,
                    FileCount = group.Count(),
                    FolderCount = folderBuckets.Count,
                    HasSameFolderDuplicates = folderBuckets.Any(bucket => bucket.Files.Count > 1),
                    HasCrossFolderDuplicates = folderBuckets.Count > 1,
                    Folders = folderBuckets,
                };
            })
            .OrderByDescending(cluster => cluster.HasSameFolderDuplicates)
            .ThenByDescending(cluster => cluster.FileCount)
            .ThenBy(cluster => cluster.ContentHash, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PostFile? GetRepresentativeFile(Post post)
        => PostDto.GetRepresentativeFile(post);

    private static int GetRepresentativeLibraryId(Post post)
        => GetRepresentativeFile(post)?.LibraryId ?? 0;

    private static string GetRepresentativeLibraryName(Post post)
        => GetRepresentativeFile(post)?.Library?.Name ?? string.Empty;

    private static string GetRepresentativeRelativePath(Post post)
        => GetRepresentativeFile(post)?.RelativePath ?? string.Empty;

    private static string GetRepresentativeContentHash(Post post)
        => GetRepresentativeFile(post)?.ContentHash ?? string.Empty;

    private static long GetRepresentativeSizeBytes(Post post)
        => GetRepresentativeFile(post)?.SizeBytes ?? 0;

    private static string GetRepresentativeContentType(Post post)
        => GetRepresentativeFile(post)?.ContentType ?? string.Empty;

    private static int GetRepresentativeWidth(Post post)
        => GetRepresentativeFile(post)?.Width ?? 0;

    private static int GetRepresentativeHeight(Post post)
        => GetRepresentativeFile(post)?.Height ?? 0;

    private static DateTime GetRepresentativeFileModifiedDate(Post post)
        => GetRepresentativeFile(post)?.FileModifiedDate ?? default;

    private static List<DuplicatePostFileDto> GetPostFileDtos(Post post)
        => post.PostFiles
            .OrderBy(file => file.Library?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Id)
            .Select(file => new DuplicatePostFileDto
            {
                PostFileId = file.Id,
                LibraryId = file.LibraryId,
                LibraryName = file.Library?.Name ?? string.Empty,
                RelativePath = file.RelativePath,
            })
            .ToList();

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

        if (!SafeSubpathResolver.TryResolve(entry.LibraryPath, entry.RelativePath, out var fullPath))
        {
            return Result<string>.Failure(OperationError.InvalidInput, "Invalid file path.");
        }

        if (!File.Exists(fullPath))
        {
            return Result<string>.Failure(OperationError.NotFound, "File not found on disk.");
        }

        return Result<string>.Success(fullPath);
    }

}
