using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

public sealed record ExternalPostReference(
    AutoTagProvider Provider,
    long ExternalPostId,
    string CanonicalUrl,
    decimal Score);
