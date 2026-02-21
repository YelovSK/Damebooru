using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Damebooru.Data;

/// <summary>
/// Ensures all DateTime values read from SQLite are marked as DateTimeKind.Utc.
/// SQLite doesn't store DateTimeKind, so without this, values come back as
/// Unspecified and get serialized without "Z" — causing browsers to interpret
/// them as local time.
/// </summary>
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    { }
}

public class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeConverter() : base(
        v => v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
    { }
}
