using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

public sealed record ExternalDiscoveryResult(
    AutoTagProvider DiscoveryProvider,
    IReadOnlyList<DiscoveredUrlMatch> Matches);
