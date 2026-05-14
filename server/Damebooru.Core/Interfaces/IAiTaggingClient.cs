using Damebooru.Core.External;

namespace Damebooru.Core.Interfaces;

public interface IAiTaggingClient
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

    Task<AiTaggingResult> TagAsync(
        Stream fileStream,
        string fileName,
        string? contentType = null,
        decimal? threshold = null,
        decimal? minConfidence = null,
        int? topK = null,
        CancellationToken cancellationToken = default);
}
