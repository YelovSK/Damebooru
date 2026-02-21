namespace Damebooru.Core.Entities;

public class ScheduledJob
{
    public int Id { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty; // Keep it simple, maybe just "Every X Minutes" or full Cron
    public bool IsEnabled { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
}
