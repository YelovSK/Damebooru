using Damebooru.Core.Interfaces;

namespace Damebooru.Core.Entities;

public class JobExecution
{
    public int Id { get; set; }
    public string JobKey { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ActivityText { get; set; }
    public string? FinalText { get; set; }
    public int? ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }
    public int? ResultSchemaVersion { get; set; }
    public string? ResultJson { get; set; }
}
