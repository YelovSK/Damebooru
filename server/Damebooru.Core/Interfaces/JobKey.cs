using System.Text.Json;
using System.Text.Json.Serialization;

namespace Damebooru.Core.Interfaces;

[JsonConverter(typeof(JobKeyJsonConverter))]
public readonly record struct JobKey
{
    public string Value { get; }

    private JobKey(string value)
    {
        Value = value;
    }

    public static JobKey Parse(string value)
    {
        if (!TryParse(value, out var key))
        {
            throw new ArgumentException("Invalid job key format.", nameof(value));
        }

        return key;
    }

    public static bool TryParse(string? value, out JobKey key)
    {
        key = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            var valid = (c >= 'a' && c <= 'z')
                        || (c >= '0' && c <= '9')
                        || c == '-';
            if (!valid)
            {
                return false;
            }
        }

        key = new JobKey(normalized);
        return true;
    }

    public override string ToString() => Value;
}

internal sealed class JobKeyJsonConverter : JsonConverter<JobKey>
{
    public override JobKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (!JobKey.TryParse(value, out var key))
        {
            throw new JsonException("Invalid job key.");
        }

        return key;
    }

    public override void Write(Utf8JsonWriter writer, JobKey value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
