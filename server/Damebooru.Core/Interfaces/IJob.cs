namespace Damebooru.Core.Interfaces;

public enum JobMode
{
    Missing,
    All,
}

public interface IJobReporter
{
    /// <summary>
    /// Replaces the job's whole live state; progress left out means indeterminate.
    /// </summary>
    void Report(string activity, int? current = null, int? total = null);
}

public sealed class NullJobReporter : IJobReporter
{
    public static readonly NullJobReporter Instance = new();

    private NullJobReporter() { }

    public void Report(string activity, int? current = null, int? total = null) { }
}

public class JobContext
{
    public string JobId { get; set; } = string.Empty;
    public CancellationToken CancellationToken { get; set; }
    public IJobReporter Reporter { get; set; } = NullJobReporter.Instance;
    public JobMode Mode { get; set; } = JobMode.Missing;
}

public interface IJob
{
    int DisplayOrder { get; }
    JobKey Key { get; }
    string Name { get; }
    string Description { get; }
    bool SupportsAllMode { get; }
    /// <returns>The summary shown once the job completes.</returns>
    Task<string> ExecuteAsync(JobContext context);
}
