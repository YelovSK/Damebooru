using System.Text.Json.Serialization;

namespace Damebooru.Core.Interfaces;

public class JobState
{
    public string? ActivityText { get; set; }
    public string? FinalText { get; set; }
    public int? ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }

    [JsonIgnore]
    public bool ClearProgressCurrent { get; set; }

    [JsonIgnore]
    public bool ClearProgressTotal { get; set; }
}
