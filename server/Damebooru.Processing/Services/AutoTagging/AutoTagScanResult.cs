using Damebooru.Core.Entities;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed record AutoTagScanResult(
    int PostId,
    AutoTagScanStatus Status,
    bool IsStaleContentReset,
    bool ShouldApply,
    AutoTagExecutionDirective? Directive);
