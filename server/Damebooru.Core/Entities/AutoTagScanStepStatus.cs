namespace Damebooru.Core.Entities;

public enum AutoTagScanStepStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    RetryableFailure = 3,
    PermanentFailure = 4,
    Skipped = 5
}
