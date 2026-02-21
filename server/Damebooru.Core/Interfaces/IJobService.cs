namespace Damebooru.Core.Interfaces;

public enum JobStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class JobInfo
{
    public string Id { get; set; } = string.Empty;
    public int ExecutionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public JobState State { get; set; } = new();
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public class JobDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool SupportsAllMode { get; set; }
}

public interface IJobService
{
    IEnumerable<JobInfo> GetActiveJobs();
    Task<(List<Entities.JobExecution> Items, int Total)> GetJobHistoryAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default);
    IEnumerable<JobDefinition> GetAvailableJobs();
    Task<string> StartJobAsync(string jobName, CancellationToken cancellationToken);
    Task<string> StartJobAsync(string jobName, CancellationToken cancellationToken, JobMode mode);
    Task<string> StartJobAsync(string jobName, Func<CancellationToken, Task> action);
    void CancelJob(string jobId);
}
