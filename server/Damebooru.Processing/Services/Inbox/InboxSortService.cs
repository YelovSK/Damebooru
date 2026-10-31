using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Services.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Services;

/// <summary>
/// Moves inbox posts into libraries, or discards them. The database is updated before files appear at their final paths,
/// so the watcher events the move causes find everything already in place and change nothing:
/// files are staged under a temporary name the watcher ignores, the post files are saved,
/// then the staged files are renamed into place and the inbox file is deleted.
/// </summary>
public class InboxSortService
{
    private readonly DamebooruDbContext _context;
    private readonly IJobService _jobService;
    private readonly IHardLinkService _hardLinkService;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly FolderTaggingService _folderTaggingService;
    private readonly ILogger<InboxSortService> _logger;

    public InboxSortService(
        DamebooruDbContext context,
        IJobService jobService,
        IHardLinkService hardLinkService,
        IFileIdentityResolver fileIdentityResolver,
        FolderTaggingService folderTaggingService,
        ILogger<InboxSortService> logger)
    {
        _context = context;
        _jobService = jobService;
        _hardLinkService = hardLinkService;
        _fileIdentityResolver = fileIdentityResolver;
        _folderTaggingService = folderTaggingService;
        _logger = logger;
    }

    public async Task<Result<List<SortInboxPostResultDto>>> SortAsync(SortInboxPostsDto dto, CancellationToken cancellationToken)
    {
        // A running scan works from a snapshot of post files taken before the move and would treat moved files as gone.
        var libraryIds = await _context.Libraries.Select(l => l.Id).ToListAsync(cancellationToken);
        if (_jobService.IsRunning(JobKeys.ScanAllLibraries) || libraryIds.Any(id => _jobService.IsRunning(JobKeys.ScanLibrary(id))))
        {
            return Result<List<SortInboxPostResultDto>>.Failure(OperationError.Conflict, "A library scan is running. Try again when it finishes.");
        }

        var results = new List<SortInboxPostResultDto>(dto.Posts.Count);
        foreach (var post in dto.Posts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await SortPostAsync(post, cancellationToken));
        }

        return Result<List<SortInboxPostResultDto>>.Success(results);
    }

    /// <summary>
    /// Deletes the posts' inbox files from disk. Posts with no other files go away with them.
    /// </summary>
    public async Task<List<InboxPostResultDto>> DiscardAsync(DiscardInboxPostsDto dto, CancellationToken cancellationToken)
    {
        var postIds = dto.PostIds.Distinct().ToList();
        var inboxFiles = await LoadInboxFilesAsync(postIds, cancellationToken);
        var failed = inboxFiles.Count > 0 ? await RemoveFilesAsync(inboxFiles) : [];

        return postIds
            .Select(postId => new InboxPostResultDto
            {
                PostId = postId,
                Error = inboxFiles.Any(f => f.PostFile.PostId == postId)
                    ? DescribeFailures(failed.Where(f => f.PostFile.PostId == postId))
                    : "Post has no files in an inbox.",
            })
            .ToList();
    }

    public async Task<List<DestinationFolderDto>> GetDestinationsAsync(CancellationToken cancellationToken)
    {
        var libraries = await _context.Libraries
            .AsNoTracking()
            .Where(l => l.Kind == LibraryKind.Library)
            .ToDictionaryAsync(l => l.Id, l => l.Name, cancellationToken);
        var files = await _context.PostFiles
            .AsNoTracking()
            .Where(pf => pf.Library.Kind == LibraryKind.Library)
            .Select(pf => new { pf.PostId, pf.LibraryId, pf.RelativePath })
            .ToListAsync(cancellationToken);

        var postsByFolder = libraries.Keys.ToDictionary(id => (LibraryId: id, Folder: string.Empty), _ => new HashSet<int>());
        foreach (var file in files)
        {
            var folder = file.RelativePath;
            do
            {
                var separator = folder.LastIndexOf('/');
                folder = separator < 0 ? string.Empty : folder[..separator];
                var key = (file.LibraryId, folder);
                (postsByFolder.TryGetValue(key, out var posts) ? posts : postsByFolder[key] = []).Add(file.PostId);
            }
            while (folder.Length > 0);
        }

        return postsByFolder
            .Select(entry => new DestinationFolderDto
            {
                LibraryId = entry.Key.LibraryId,
                LibraryName = libraries[entry.Key.LibraryId],
                Folder = entry.Key.Folder,
                PostCount = entry.Value.Count,
            })
            .OrderBy(d => d.LibraryName)
            .ThenBy(d => d.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<SortInboxPostResultDto> SortPostAsync(SortInboxPostDto dto, CancellationToken cancellationToken)
    {
        var result = new SortInboxPostResultDto { PostId = dto.PostId };

        var inboxFiles = await LoadInboxFilesAsync([dto.PostId], cancellationToken);
        if (inboxFiles.Count == 0)
        {
            result.Error = "Post has no files in an inbox.";
            return result;
        }

        var source = inboxFiles.FirstOrDefault(f => File.Exists(f.FullPath));
        if (source == null)
        {
            result.Error = "The inbox file is missing on disk.";
            return result;
        }

        var postFile = source.PostFile;
        var sourcePath = source.FullPath;
        var targets = await PlanTargetsAsync(dto.Destinations, Path.GetFileName(sourcePath), cancellationToken);
        if (!targets.IsSuccess)
        {
            result.Error = targets.Message;
            return result;
        }

        try
        {
            StageFiles(sourcePath, targets.Value!);
        }
        catch (Exception ex)
        {
            DeleteQuietly(targets.Value!.Select(t => t.StagedPath));
            _logger.LogWarning(ex, "Failed to stage {Path} for sorting", sourcePath);
            result.Error = $"Failed to copy the file: {ex.Message}";
            return result;
        }

        var original = (postFile.LibraryId, postFile.RelativePath, postFile.FileIdentityDevice, postFile.FileIdentityValue, postFile.FileModifiedDate);
        List<PostFile> copies;
        try
        {
            copies = await SavePostFilesAsync(postFile, targets.Value!);
        }
        catch (Exception ex)
        {
            DeleteQuietly(targets.Value!.Select(t => t.StagedPath));
            // Unsaved changes would otherwise be saved along with the next post in the batch.
            _context.ChangeTracker.Clear();
            _logger.LogWarning(ex, "Failed to save sorted post files for {Path}", sourcePath);
            result.Error = $"Failed to save the new location: {ex.Message}";
            return result;
        }

        var placed = new List<Target>();
        try
        {
            foreach (var target in targets.Value!)
            {
                File.Move(target.StagedPath, target.FullPath, overwrite: false);
                placed.Add(target);
            }
        }
        catch (Exception ex)
        {
            DeleteQuietly(placed.Select(t => t.FullPath));
            DeleteQuietly(targets.Value!.Except(placed).Select(t => t.StagedPath));
            (postFile.LibraryId, postFile.RelativePath, postFile.FileIdentityDevice, postFile.FileIdentityValue, postFile.FileModifiedDate) = original;
            _context.PostFiles.RemoveRange(copies);
            await _context.SaveChangesAsync(CancellationToken.None);
            await _folderTaggingService.SyncPostFolderTagsAsync(_context, [postFile.PostId], CancellationToken.None);
            await _context.SaveChangesAsync(CancellationToken.None);

            _logger.LogWarning(ex, "Failed to move {Path} into place", sourcePath);
            result.Error = $"Failed to move the file into place: {ex.Message}";
            return result;
        }

        var warnings = new List<string>();
        try
        {
            File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            // The leftover is picked up again as another file of the same post.
            _logger.LogWarning(ex, "Sorted {Path} but could not delete it from the inbox", sourcePath);
            warnings.Add($"The inbox file could not be deleted: {ex.Message}");
        }

        var duplicates = inboxFiles.Where(f => f != source).ToList();
        if (duplicates.Count > 0 && DescribeFailures(await RemoveFilesAsync(duplicates)) is { } duplicateError)
        {
            warnings.Add(duplicateError);
        }

        result.Warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
        result.Locations = targets.Value!
            .Select(t => new SortedFileLocationDto { LibraryId = t.Library.Id, RelativePath = t.RelativePath })
            .ToList();
        return result;
    }

    private async Task<List<InboxFile>> LoadInboxFilesAsync(IReadOnlyCollection<int> postIds, CancellationToken cancellationToken)
    {
        var inboxPaths = await _context.Libraries
            .AsNoTracking()
            .Where(l => l.Kind == LibraryKind.Inbox)
            .ToDictionaryAsync(l => l.Id, l => l.Path, cancellationToken);
        var inboxIds = inboxPaths.Keys.ToList();
        var postFiles = await _context.PostFiles
            .Where(pf => postIds.Contains(pf.PostId) && inboxIds.Contains(pf.LibraryId))
            .OrderBy(pf => pf.Id)
            .ToListAsync(cancellationToken);

        return postFiles
            .Select(pf => SafeSubpathResolver.TryResolve(inboxPaths[pf.LibraryId], pf.RelativePath, out var fullPath)
                ? new InboxFile(pf, fullPath)
                : null)
            .OfType<InboxFile>()
            .ToList();
    }

    /// <summary>
    /// Forgets the files before deleting them, so the watcher's delete events find nothing left to do.
    /// </summary>
    /// <returns>The files that couldn't be deleted from disk.</returns>
    private async Task<List<InboxFile>> RemoveFilesAsync(IReadOnlyCollection<InboxFile> files)
    {
        _context.PostFiles.RemoveRange(files.Select(f => f.PostFile));
        await _context.SaveChangesAsync(CancellationToken.None);
        await PostFileWriter.DeleteEmptyPostsAsync(_context, CancellationToken.None);

        var failed = new List<InboxFile>();
        foreach (var file in files)
        {
            try
            {
                File.Delete(file.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete inbox file {Path}", file.FullPath);
                failed.Add(file);
            }
        }

        return failed;
    }

    private static string? DescribeFailures(IEnumerable<InboxFile> failed)
    {
        var paths = failed.Select(f => f.PostFile.RelativePath).ToList();
        return paths.Count > 0 ? $"Could not delete {string.Join(", ", paths)}; a later scan will pick them up again." : null;
    }

    private async Task<Result<List<Target>>> PlanTargetsAsync(
        IReadOnlyCollection<SortDestinationDto> destinations,
        string fileName,
        CancellationToken cancellationToken)
    {
        var distinct = destinations
            .Select(d => (d.LibraryId, Folder: RelativePathMatcher.NormalizePath(d.Folder ?? string.Empty)))
            .Distinct()
            .ToList();
        if (distinct.Count == 0)
        {
            return Result<List<Target>>.Failure(OperationError.InvalidInput, "No destinations given.");
        }

        var libraryIds = distinct.Select(d => d.LibraryId).Distinct().ToList();
        var libraries = await _context.Libraries
            .AsNoTracking()
            .Where(l => libraryIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, cancellationToken);

        var targets = new List<Target>(distinct.Count);
        foreach (var (libraryId, folder) in distinct)
        {
            if (!libraries.TryGetValue(libraryId, out var library))
            {
                return Result<List<Target>>.Failure(OperationError.NotFound, $"Library {libraryId} not found.");
            }

            if (library.Kind != LibraryKind.Library)
            {
                return Result<List<Target>>.Failure(OperationError.InvalidInput, $"\"{library.Name}\" is an inbox, not a library.");
            }

            if (!SafeSubpathResolver.TryResolve(library.Path, folder, out var directoryPath))
            {
                return Result<List<Target>>.Failure(OperationError.InvalidInput, $"Folder \"{folder}\" is outside \"{library.Name}\".");
            }

            var relativePath = await PickFreeRelativePathAsync(library, folder, directoryPath, fileName, cancellationToken);
            targets.Add(new Target(
                library,
                relativePath,
                Path.Combine(directoryPath, Path.GetFileName(relativePath)),
                Path.Combine(directoryPath, $".damebooru-sort-{Guid.NewGuid():N}.tmp")));
        }

        return Result<List<Target>>.Success(targets);
    }

    private async Task<string> PickFreeRelativePathAsync(
        Library library,
        string folder,
        string directoryPath,
        string fileName,
        CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 1; ; number++)
        {
            var candidateName = number == 1 ? fileName : $"{stem} ({number}){extension}";
            var relativePath = string.IsNullOrEmpty(folder) ? candidateName : $"{folder}/{candidateName}";
            var taken = File.Exists(Path.Combine(directoryPath, candidateName))
                || await _context.PostFiles.AnyAsync(pf => pf.LibraryId == library.Id && pf.RelativePath == relativePath, cancellationToken);
            if (!taken)
            {
                return relativePath;
            }
        }
    }

    /// <summary>
    /// The file moved to the first target is hardlinked when possible, so a move within one volume copies nothing;
    /// deleting the inbox file afterwards leaves it as an ordinary file. Other targets get real copies.
    /// </summary>
    private void StageFiles(string sourcePath, IReadOnlyList<Target> targets)
    {
        var modifiedUtc = File.GetLastWriteTimeUtc(sourcePath);
        var createdUtc = File.GetCreationTimeUtc(sourcePath);

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            Directory.CreateDirectory(Path.GetDirectoryName(target.StagedPath)!);
            if (i == 0 && _hardLinkService.CreateHardLink(target.StagedPath, sourcePath).Success)
            {
                continue;
            }

            File.Copy(sourcePath, target.StagedPath);
            File.SetCreationTimeUtc(target.StagedPath, createdUtc);
            File.SetLastWriteTimeUtc(target.StagedPath, modifiedUtc);
        }
    }

    private async Task<List<PostFile>> SavePostFilesAsync(PostFile postFile, IReadOnlyList<Target> targets)
    {
        ApplyTarget(postFile, targets[0]);

        var copies = targets
            .Skip(1)
            .Select(target =>
            {
                var copy = new PostFile { PostId = postFile.PostId };
                ApplyTarget(copy, target);
                return copy;
            })
            .ToList();
        _context.PostFiles.AddRange(copies);

        await using var transaction = await _context.Database.BeginTransactionAsync();
        await _context.SaveChangesAsync();
        await _folderTaggingService.SyncPostFolderTagsAsync(_context, [postFile.PostId]);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return copies;
    }

    private void ApplyTarget(PostFile postFile, Target target)
    {
        var identity = _fileIdentityResolver.TryResolve(target.StagedPath);
        postFile.LibraryId = target.Library.Id;
        postFile.RelativePath = target.RelativePath;
        postFile.FileIdentityDevice = identity?.Device;
        postFile.FileIdentityValue = identity?.Value;
        postFile.FileModifiedDate = File.GetLastWriteTimeUtc(target.StagedPath);
    }

    private void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up {Path}", path);
            }
        }
    }

    private sealed record Target(Library Library, string RelativePath, string FullPath, string StagedPath);

    private sealed record InboxFile(PostFile PostFile, string FullPath);
}
