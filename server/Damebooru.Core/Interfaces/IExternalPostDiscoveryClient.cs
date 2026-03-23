using Damebooru.Core.Entities;
using Damebooru.Core.External;

namespace Damebooru.Core.Interfaces;

public interface IExternalPostDiscoveryClient
{
    AutoTagProvider Provider { get; }
    Task<ExternalDiscoveryResult> DiscoverAsync(PostDiscoveryContext context, CancellationToken cancellationToken = default);
}
