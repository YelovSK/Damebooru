namespace Damebooru.Core.Entities;

public enum AutoTagScanStatus
{
    Pending = 0,
    InProgress = 1,
    Partial = 2,
    Completed = 3,
    Failed = 4
}
