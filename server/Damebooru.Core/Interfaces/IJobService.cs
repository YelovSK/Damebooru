namespace Damebooru.Core.Interfaces;

public enum JobStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public class JobInfo
{
    public string Id { get; set; } = string.Empty;
    public int ExecutionId { get; set; }
    public JobKey Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public JobState State { get; set; } = new();
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public class JobDefinition
{
    public JobKey Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool SupportsAllMode { get; set; }
    public bool Schedulable { get; set; }
}

public interface IJobService
{
    IEnumerable<JobInfo> GetActiveJobs();
    Task<(List<Entities.JobExecution> Items, int Total)> GetJobHistoryAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default);
    IEnumerable<JobDefinition> GetAvailableJobs();
    bool IsRunning(JobKey jobKey);
    Task<string> StartJobAsync(JobKey jobKey, JobMode mode = JobMode.Missing);

    /// <summary>
    /// Runs an ad-hoc job that isn't registered as an <see cref="IJob"/>.
    /// </summary>
    Task<string> StartJobAsync(JobKey jobKey, Func<JobContext, Task<string>> execute);
    void CancelJob(string jobId);
}
