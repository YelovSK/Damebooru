using Damebooru.Core.Interfaces;

namespace Damebooru.Core.Interfaces;

public enum JobMode
{
    /// <summary>Only process items that haven't been processed yet.</summary>
    Missing,
    /// <summary>Reprocess all items, regenerating existing data.</summary>
    All
}

public class JobContext
{
    public string JobId { get; set; } = string.Empty;
    public CancellationToken CancellationToken { get; set; }
    public IProgress<JobState> State { get; set; } = new Progress<JobState>();
    public JobMode Mode { get; set; } = JobMode.Missing;
}

public interface IJob
{
    int DisplayOrder { get; }
    string Key { get; }
    string Name { get; }
    string Description { get; }
    bool SupportsAllMode { get; }
    Task ExecuteAsync(JobContext context);
}
