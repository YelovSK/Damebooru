using Bakabooru.Core.Entities;

namespace Bakabooru.Core.Interfaces;

public interface ILibrarySyncProcessor
{
    /// <summary>
     /// Processes a single file.
    /// </summary>
    Task ProcessFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Processes all files in a directory.
    /// </summary>
    Task ProcessDirectoryAsync(Library library, string directoryPath, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default);
}
