using Damebooru.Core.External;

namespace Damebooru.Core.Interfaces;

public interface ISauceNaoClient
{
    Task<SauceNaoSearchResult> SearchAsync(
        Stream fileStream,
        string fileName,
        string? contentType = null,
        CancellationToken cancellationToken = default);
}
