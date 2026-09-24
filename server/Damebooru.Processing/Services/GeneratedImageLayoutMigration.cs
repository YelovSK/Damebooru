using Damebooru.Core.Paths;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Services;

/// <summary>
/// Generated images used to be stored per library ({root}/{libraryId}/{hash}.webp) and are now keyed by content hash only.
/// Moves files left in the old layout; safe to run on every startup.
/// </summary>
public static class GeneratedImageLayoutMigration
{
    public static void Run(string previewRootPath, string thumbnailRootPath, ILogger logger)
    {
        MoveUp(previewRootPath, logger);
        MoveUp(Path.Combine(thumbnailRootPath, MediaPaths.ThumbnailSizeSegment), logger);
    }

    private static void MoveUp(string rootPath, ILogger logger)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var libraryDirectory in Directory.EnumerateDirectories(rootPath).Where(dir => int.TryParse(Path.GetFileName(dir), out _)))
        {
            var moved = 0;
            foreach (var file in Directory.EnumerateFiles(libraryDirectory, MediaPaths.GeneratedImageGlobPattern))
            {
                var destination = Path.Combine(rootPath, Path.GetFileName(file));
                if (File.Exists(destination))
                {
                    File.Delete(file);
                }
                else
                {
                    File.Move(file, destination);
                    moved++;
                }
            }

            if (!Directory.EnumerateFileSystemEntries(libraryDirectory).Any())
            {
                Directory.Delete(libraryDirectory);
            }

            logger.LogInformation("Moved {Count} generated images from {Path} to the hash-only layout", moved, libraryDirectory);
        }
    }
}
