using Damebooru.Core.Interfaces;

namespace Damebooru.Core.Entities;

public class JobExecution
{
    public int Id { get; set; }
    public string JobName { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResultData { get; set; } // Serialized JobState snapshot
}
