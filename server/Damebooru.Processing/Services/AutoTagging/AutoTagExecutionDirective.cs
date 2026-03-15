using Damebooru.Core.Entities;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed record AutoTagExecutionDirective(
    AutoTagProvider Provider,
    string Reason,
    TimeSpan? Delay,
    bool StopCurrentRun);
