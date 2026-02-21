namespace Damebooru.Core.Interfaces;

public class MediaSourceItem
{
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
}

public interface IMediaSource
{
    // Returns an async enumerable of items found in the source
    IAsyncEnumerable<MediaSourceItem> GetItemsAsync(string sourcePath, CancellationToken cancellationToken);
    
    // Returns the total count of items in the source
    Task<int> CountAsync(string sourcePath, CancellationToken cancellationToken);
}
