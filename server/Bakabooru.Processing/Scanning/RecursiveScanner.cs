using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Bakabooru.Processing.Scanning;

public class RecursiveScanner : IScannerService
{
    private readonly ILogger<RecursiveScanner> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILibrarySyncProcessor _librarySyncProcessor;

    public RecursiveScanner(
        ILogger<RecursiveScanner> logger,
        IServiceScopeFactory scopeFactory,
        ILibrarySyncProcessor librarySyncProcessor)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _librarySyncProcessor = librarySyncProcessor;
    }
    public async Task ScanAllLibrariesAsync(IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        
        var libraries = await dbContext.Libraries.ToListAsync(cancellationToken);
        _logger.LogInformation("Found {Count} libraries to scan.", libraries.Count);

        if (libraries.Count == 0)
        {
            status?.Report("No libraries configured.");
            progress?.Report(100);
            return;
        }
        
        int totalLibraries = libraries.Count;
        int currentLibraryIndex = 0;

        foreach (var library in libraries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var displayName = GetLibraryDisplayName(library.Name, library.Path, library.Id);
            _logger.LogInformation("Starting scan for library {Id}: {Path}", library.Id, library.Path);
            status?.Report($"Scanning library: {displayName}");
            
            // Create a sub-progress that maps 0-100 of this library to a slice of the total progress
            var subProgress = new Progress<float>(percent => 
            {
                if (progress != null)
                {
                    float baseProgress = (float)currentLibraryIndex / totalLibraries * 100;
                    float slice = 100f / totalLibraries;
                    float currentTotal = baseProgress + (percent / 100f * slice);
                    progress.Report(currentTotal);
                }
            });

            await _librarySyncProcessor.ProcessDirectoryAsync(library, library.Path, subProgress, status, cancellationToken);
            currentLibraryIndex++;
        }
    }

    public async Task ScanLibraryAsync(int libraryId, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        
        var library = await dbContext.Libraries.FindAsync(new object[] { libraryId }, cancellationToken);
        if (library == null)
        {
            _logger.LogWarning("Library {LibraryId} not found.", libraryId);
            return;
        }

        var displayName = GetLibraryDisplayName(library.Name, library.Path, library.Id);
        _logger.LogInformation("Scanning library: {Path}", library.Path);
        status?.Report($"Scanning library: {displayName}");
        await _librarySyncProcessor.ProcessDirectoryAsync(library, library.Path, progress, status, cancellationToken);
        progress?.Report(100);
        status?.Report($"Completed scan for: {displayName}");
        _logger.LogInformation("Finished scanning library: {Path}", library.Path);
    }

    private static string GetLibraryDisplayName(string? name, string path, int id)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (!string.IsNullOrWhiteSpace(folder))
            return folder;

        return $"Library #{id}";
    }
}
