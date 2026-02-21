using Damebooru.Core;
using Damebooru.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Scanning;

public class FileSystemMediaSource : IMediaSource
{
    private readonly ILogger<FileSystemMediaSource> _logger;

    public FileSystemMediaSource(ILogger<FileSystemMediaSource> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<MediaSourceItem> GetItemsAsync(string sourcePath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourcePath))
        {
            _logger.LogWarning("Source directory not found: {Path}", sourcePath);
            yield break;
        }
        
        // We use Task.Run to offload the synchronous enumeration to a thread pool thread
        // to avoid blocking the caller if the directory is large or on a network share.
        // However, EnumerateFiles itself is lazy.
        
        var options = new EnumerationOptions 
        { 
            IgnoreInaccessible = true, 
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.Temporary
        };

        IEnumerable<string> files;
        try 
        {
             files = Directory.EnumerateFiles(sourcePath, "*.*", options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate files in {Path}", sourcePath);
            throw;
        }

        foreach (var filePath in files)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            if (IsAllowed(filePath))
            {
                FileInfo fi;
                try 
                {
                    fi = new FileInfo(filePath);
                }
                catch
                {
                    continue; // Skip files we can't stat
                }

                yield return new MediaSourceItem
                {
                    FullPath = filePath,
                    RelativePath = Path.GetRelativePath(sourcePath, filePath),
                    SizeBytes = fi.Length,
                    LastModifiedUtc = fi.LastWriteTimeUtc
                };
            }
        }
    }

    public async Task<int> CountAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourcePath)) return 0;

        return await Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.Temporary
            };

            int count = 0;
            // Iterate only file names, no need to construct FileInfo or yield objects.
            // Just count matches.
            foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*.*", options))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (IsAllowed(filePath)) count++;
            }
            return count;
        });
    }

    private static bool IsAllowed(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedMedia.IsSupported(ext);
    }
}
