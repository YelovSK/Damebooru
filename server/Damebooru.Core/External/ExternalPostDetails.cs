using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

public sealed record ExternalPostDetails(
    AutoTagProvider Provider,
    long PostId,
    string CanonicalUrl,
    IReadOnlyList<string> SourceUrls,
    IReadOnlyList<ExternalTagData> Tags);
