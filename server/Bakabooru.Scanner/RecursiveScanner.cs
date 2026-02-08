using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bakabooru.Scanner;

public class RecursiveScanner : IScannerService
{
    private readonly ILogger<RecursiveScanner> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHasherService _hasher;
    private readonly IImageProcessor _imageProcessor;
    private readonly ISimilarityService _similarityService;

    public RecursiveScanner(
        ILogger<RecursiveScanner> logger,
        IServiceScopeFactory scopeFactory,
        IHasherService hasher,
        IImageProcessor imageProcessor,
        ISimilarityService similarityService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _hasher = hasher;
        _imageProcessor = imageProcessor;
        _similarityService = similarityService;
    }

    public async Task ScanAllLibrariesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        
        var libraries = await dbContext.Libraries.ToListAsync(cancellationToken);
        
        foreach (var library in libraries)
        {
            await ScanLibraryAsync(library.Id, cancellationToken);
        }
    }

    public async Task ScanLibraryAsync(int libraryId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        
        var library = await dbContext.Libraries.FindAsync(new object[] { libraryId }, cancellationToken);
        if (library == null)
        {
            _logger.LogWarning("Library {LibraryId} not found.", libraryId);
            return;
        }

        if (!Directory.Exists(library.Path))
        {
            _logger.LogWarning("Library path {Path} does not exist.", library.Path);
            return;
        }

        _logger.LogInformation("Scanning library: {Path}", library.Path);

        // Recursive scan
        await ProcessDirectoryAsync(dbContext, library, library.Path, cancellationToken);
        
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Finished scanning library: {Path}", library.Path);
    }

    private async Task ProcessDirectoryAsync(BakabooruDbContext dbContext, Library library, string currentPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var files = Directory.GetFiles(currentPath);
            foreach (var file in files)
            {
                await ProcessFileAsync(dbContext, library, file, cancellationToken);
            }

            var subDirectories = Directory.GetDirectories(currentPath);
            foreach (var subDir in subDirectories)
            {
                await ProcessDirectoryAsync(dbContext, library, subDir, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory {Path}", currentPath);
        }
    }

    private async Task ProcessFileAsync(BakabooruDbContext dbContext, Library library, string filePath, CancellationToken cancellationToken)
    {
        // Simple extension filter for now
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".mp4", ".webm" };
        
        if (!allowedExtensions.Contains(extension))
        {
            return;
        }

        var relativePath = Path.GetRelativePath(library.Path, filePath);
        
        // Check if exists by path
        var existingPost = await dbContext.Posts
            .FirstOrDefaultAsync(p => p.LibraryId == library.Id && p.RelativePath == relativePath, cancellationToken);

        if (existingPost != null)
        {
            // Update if needed (e.g., check modification time or re-hash logic currently skipped for optimization)
            return;
        }

        try 
        {
            var hash = await _hasher.ComputeMd5Async(filePath, cancellationToken);
            var fileInfo = new FileInfo(filePath);

            int width = 0;
            int height = 0;
            ulong perceptualHash = 0;

            if (ContentTypeIsImage(extension))
            {
                var metadata = await _imageProcessor.GetMetadataAsync(filePath, cancellationToken);
                width = metadata.Width;
                height = metadata.Height;
                
                perceptualHash = await _similarityService.ComputeHashAsync(filePath, cancellationToken);
            }

            var post = new Post
            {
                LibraryId = library.Id,
                RelativePath = relativePath,
                Md5Hash = hash,
                PerceptualHash = perceptualHash,
                SizeBytes = fileInfo.Length,
                ContentType = GetMimeType(extension),
                ImportDate = DateTime.UtcNow,
                Width = width,
                Height = height
            };

            dbContext.Posts.Add(post);
            // Save periodically or at end? For now, we save at end of library scan, 
            // but tracking thousands of entities might be heavy.
            // Let's add and rely on final SaveChanges or intermediate batching if needed.
            
            _logger.LogDebug("Found new file: {Path}", relativePath);
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Error processing file {Path}", filePath);
        }
    }

    private string GetMimeType(string extension)
    {
        return extension switch
        {
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    private bool ContentTypeIsImage(string extension)
    {
        return extension == ".jpg" || extension == ".jpeg" || extension == ".png" || extension == ".gif";
    }
}
