namespace Damebooru.Core.Results;

/// <summary>
/// Aggregated result of a library scan operation.
/// </summary>
public record ScanResult(int Scanned, int Added, int Updated, int Moved, int Removed)
{
    public static ScanResult Empty => new(0, 0, 0, 0, 0);

    public static ScanResult operator +(ScanResult a, ScanResult b) =>
        new(a.Scanned + b.Scanned,
            a.Added + b.Added,
            a.Updated + b.Updated,
            a.Moved + b.Moved,
            a.Removed + b.Removed);
}
