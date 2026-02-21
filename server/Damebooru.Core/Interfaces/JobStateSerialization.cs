using System.Text.Json;
using System.Text.Json.Serialization;

namespace Damebooru.Core.Interfaces;

public static class JobStateSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(JobState state)
    {
        return JsonSerializer.Serialize(state, Options);
    }

    public static JobState? Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JobState>(value, Options);
        }
        catch
        {
            return new JobState
            {
                Phase = "Completed",
                Summary = value
            };
        }
    }
}
