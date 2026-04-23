using Damebooru.Core.Entities;
using Damebooru.Core.Results;

namespace Damebooru.Core.Interfaces;

public interface ILibrarySyncProcessor
{
    /// <summary>
     /// Processes a single file using upsert-style behavior.
     /// </summary>
    Task ProcessFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a newly created file.
    /// </summary>
    Task ProcessCreatedFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a changed file.
    /// </summary>
    Task ProcessChangedFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a deleted file by its prior relative path.
    /// </summary>
    Task ProcessDeletedFileAsync(Library library, string relativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a deleted directory by its prior relative path prefix.
    /// </summary>
    Task ProcessDeletedDirectoryAsync(Library library, string relativePathPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a moved or renamed file.
    /// </summary>
    Task ProcessMovedFileAsync(Library library, string oldRelativePath, MediaSourceItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a moved or renamed directory within a library.
    /// </summary>
    Task ProcessMovedDirectoryAsync(Library library, string oldRelativePathPrefix, string newRelativePathPrefix, CancellationToken cancellationToken);

    /// <summary>
     /// Processes all files in a directory.
     /// </summary>
    Task<ScanResult> ProcessDirectoryAsync(Library library, string directoryPath, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default);
}
