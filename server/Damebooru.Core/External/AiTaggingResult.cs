using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

public sealed record AiTaggingResult(
    string Model,
    string Provider,
    decimal Threshold,
    decimal MinConfidence,
    decimal ElapsedMilliseconds,
    IReadOnlyList<AiTagData> Tags);

public sealed record AiTagData(
    string Name,
    decimal Score,
    TagCategoryKind Category,
    string RawCategory);
