using Damebooru.Core.Entities;
using Damebooru.Core.External;

namespace Damebooru.Core.Interfaces;

public interface IExternalPostMetadataClient
{
    AutoTagProvider Provider { get; }
    ExternalPostReference? TryParseReference(string url, decimal score);
    Task<ExternalPostDetails?> GetPostDetailsAsync(long postId, CancellationToken cancellationToken = default);
}
