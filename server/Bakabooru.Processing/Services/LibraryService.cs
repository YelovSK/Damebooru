using Bakabooru.Core.DTOs;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
using Bakabooru.Core.Paths;
using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public class LibraryService
{
    private readonly BakabooruDbContext _context;
    private readonly IJobService _jobService;
    private readonly IScannerService _scannerService;

    public LibraryService(BakabooruDbContext context, IJobService jobService, IScannerService scannerService)
    {
        _context = context;
        _jobService = jobService;
        _scannerService = scannerService;
    }

    public async Task<List<LibraryDto>> GetLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var libraries = await _context.Libraries
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var stats = await _context.Posts
            .AsNoTracking()
            .GroupBy(p => p.LibraryId)
            .Select(g => new
            {
                LibraryId = g.Key,
                PostCount = g.Count(),
                TotalSizeBytes = g.Sum(p => p.SizeBytes),
                LastImportDate = g.Max(p => p.ImportDate)
            })
            .ToDictionaryAsync(x => x.LibraryId, cancellationToken);

        var ignoredPathsRaw = await _context.LibraryIgnoredPaths
            .AsNoTracking()
            .Select(p => new { p.LibraryId, p.Id, p.RelativePathPrefix, p.CreatedDate })
            .ToListAsync(cancellationToken);

        var ignoredPathsByLibrary = ignoredPathsRaw
            .GroupBy(p => p.LibraryId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(p => p.RelativePathPrefix)
                    .Select(p => new LibraryIgnoredPathDto
                    {
                        Id = p.Id,
                        Path = p.RelativePathPrefix,
                        CreatedDate = p.CreatedDate
                    })
                    .ToList());

        return libraries.Select(l =>
        {
            stats.TryGetValue(l.Id, out var s);
            ignoredPathsByLibrary.TryGetValue(l.Id, out var ignoredPaths);
            return new LibraryDto
            {
                Id = l.Id,
                Name = l.Name,
                Path = l.Path,
                ScanIntervalHours = l.ScanInterval.TotalHours,
                PostCount = s?.PostCount ?? 0,
                TotalSizeBytes = s?.TotalSizeBytes ?? 0,
                LastImportDate = s?.LastImportDate,
                IgnoredPaths = ignoredPaths ?? []
            };
        }).ToList();
    }

    public async Task<Result<LibraryDto>> GetLibraryAsync(int id, CancellationToken cancellationToken = default)
    {
        var library = await _context.Libraries.FindAsync(new object[] { id }, cancellationToken);
        if (library == null)
        {
            return Result<LibraryDto>.Failure(OperationError.NotFound, "Library not found.");
        }

        return Result<LibraryDto>.Success(await BuildLibraryDtoAsync(library, cancellationToken));
    }

    public async Task<Result<LibraryDto>> CreateLibraryAsync(CreateLibraryDto dto)
    {
        if (!Directory.Exists(dto.Path))
        {
            return Result<LibraryDto>.Failure(OperationError.InvalidInput, $"Directory specified does not exist: {dto.Path}");
        }

        var library = new Library
        {
            Name = string.IsNullOrWhiteSpace(dto.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(dto.Path))
                : dto.Name.Trim(),
            Path = dto.Path,
            ScanInterval = TimeSpan.FromHours(1)
        };

        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        return Result<LibraryDto>.Success(new LibraryDto
        {
            Id = library.Id,
            Name = library.Name,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours,
            PostCount = 0,
            TotalSizeBytes = 0,
            LastImportDate = null,
            IgnoredPaths = []
        });
    }

    public async Task<Result<List<LibraryIgnoredPathDto>>> GetIgnoredPathsAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        var libraryExists = await _context.Libraries
            .AsNoTracking()
            .AnyAsync(l => l.Id == libraryId, cancellationToken);
        if (!libraryExists)
        {
            return Result<List<LibraryIgnoredPathDto>>.Failure(OperationError.NotFound, "Library not found.");
        }

        var ignoredPaths = await _context.LibraryIgnoredPaths
            .AsNoTracking()
            .Where(p => p.LibraryId == libraryId)
            .OrderBy(p => p.RelativePathPrefix)
            .Select(p => new LibraryIgnoredPathDto
            {
                Id = p.Id,
                Path = p.RelativePathPrefix,
                CreatedDate = p.CreatedDate
            })
            .ToListAsync(cancellationToken);

        return Result<List<LibraryIgnoredPathDto>>.Success(ignoredPaths);
    }

    public async Task<Result<AddLibraryIgnoredPathResultDto>> AddIgnoredPathAsync(int libraryId, AddLibraryIgnoredPathDto dto)
    {
        var libraryExists = await _context.Libraries.AnyAsync(l => l.Id == libraryId);
        if (!libraryExists)
        {
            return Result<AddLibraryIgnoredPathResultDto>.Failure(OperationError.NotFound, "Library not found.");
        }

        var normalizedPath = RelativePathMatcher.NormalizePath(dto.Path);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return Result<AddLibraryIgnoredPathResultDto>.Failure(OperationError.InvalidInput, "Path cannot be empty.");
        }

        var existingIgnoredPaths = await _context.LibraryIgnoredPaths
            .Where(p => p.LibraryId == libraryId)
            .ToListAsync();

        var ignoredPath = existingIgnoredPaths.FirstOrDefault(p =>
            string.Equals(p.RelativePathPrefix, normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (ignoredPath == null)
        {
            ignoredPath = new LibraryIgnoredPath
            {
                LibraryId = libraryId,
                RelativePathPrefix = normalizedPath,
                CreatedDate = DateTime.UtcNow
            };
            _context.LibraryIgnoredPaths.Add(ignoredPath);
            await _context.SaveChangesAsync();
        }

        var removedPostCount = await DeletePostsInIgnoredPathAsync(libraryId, normalizedPath);

        return Result<AddLibraryIgnoredPathResultDto>.Success(new AddLibraryIgnoredPathResultDto
        {
            IgnoredPath = new LibraryIgnoredPathDto
            {
                Id = ignoredPath.Id,
                Path = ignoredPath.RelativePathPrefix,
                CreatedDate = ignoredPath.CreatedDate
            },
            RemovedPostCount = removedPostCount
        });
    }

    public async Task<Result> DeleteIgnoredPathAsync(int libraryId, int ignoredPathId)
    {
        var ignoredPath = await _context.LibraryIgnoredPaths
            .FirstOrDefaultAsync(p => p.Id == ignoredPathId && p.LibraryId == libraryId);
        if (ignoredPath == null)
        {
            return Result.Failure(OperationError.NotFound, "Ignored path not found.");
        }

        _context.LibraryIgnoredPaths.Remove(ignoredPath);
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<string>> ScanLibraryAsync(int libraryId)
    {
        var libraryExists = await _context.Libraries.AnyAsync(l => l.Id == libraryId);
        if (!libraryExists)
        {
            return Result<string>.Failure(OperationError.NotFound, "Library not found.");
        }

        var jobName = $"Scan Library #{libraryId}";
        var jobId = await _jobService.StartJobAsync(
            jobName,
            ct => _scannerService.ScanLibraryAsync(libraryId, cancellationToken: ct));

        return Result<string>.Success(jobId);
    }

    public async Task<Result> DeleteLibraryAsync(int id)
    {
        var library = await _context.Libraries.FindAsync(new object[] { id });
        if (library == null)
        {
            return Result.Failure(OperationError.NotFound, "Library not found.");
        }

        _context.Libraries.Remove(library);
        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<LibraryDto>> RenameLibraryAsync(int id, RenameLibraryDto dto)
    {
        var library = await _context.Libraries.FindAsync(new object[] { id });
        if (library == null)
        {
            return Result<LibraryDto>.Failure(OperationError.NotFound, "Library not found.");
        }

        library.Name = dto.Name.Trim();
        await _context.SaveChangesAsync();

        return Result<LibraryDto>.Success(await BuildLibraryDtoAsync(library, CancellationToken.None));
    }

    private async Task<LibraryDto> BuildLibraryDtoAsync(Library library, CancellationToken cancellationToken)
    {
        var ignoredPaths = await _context.LibraryIgnoredPaths
            .AsNoTracking()
            .Where(p => p.LibraryId == library.Id)
            .OrderBy(p => p.RelativePathPrefix)
            .Select(p => new LibraryIgnoredPathDto
            {
                Id = p.Id,
                Path = p.RelativePathPrefix,
                CreatedDate = p.CreatedDate
            })
            .ToListAsync(cancellationToken);

        return new LibraryDto
        {
            Id = library.Id,
            Name = library.Name,
            Path = library.Path,
            ScanIntervalHours = library.ScanInterval.TotalHours,
            PostCount = await _context.Posts.CountAsync(p => p.LibraryId == library.Id, cancellationToken),
            TotalSizeBytes = await _context.Posts
                .Where(p => p.LibraryId == library.Id)
                .Select(p => (long?)p.SizeBytes)
                .SumAsync(cancellationToken) ?? 0,
            LastImportDate = await _context.Posts
                .Where(p => p.LibraryId == library.Id)
                .MaxAsync(p => (DateTime?)p.ImportDate, cancellationToken),
            IgnoredPaths = ignoredPaths
        };
    }

    private async Task<int> DeletePostsInIgnoredPathAsync(int libraryId, string normalizedPathPrefix)
    {
        var postCandidates = await _context.Posts
            .AsNoTracking()
            .Where(p => p.LibraryId == libraryId)
            .Select(p => new { p.Id, p.RelativePath })
            .ToListAsync();

        var idsToDelete = postCandidates
            .Where(p => RelativePathMatcher.IsWithinPrefix(p.RelativePath, normalizedPathPrefix))
            .Select(p => p.Id)
            .ToList();

        var removed = 0;
        const int batchSize = 500;
        for (var i = 0; i < idsToDelete.Count; i += batchSize)
        {
            var batch = idsToDelete.Skip(i).Take(batchSize).ToList();
            removed += await _context.Posts
                .Where(p => batch.Contains(p.Id))
                .ExecuteDeleteAsync();
        }

        return removed;
    }
}

