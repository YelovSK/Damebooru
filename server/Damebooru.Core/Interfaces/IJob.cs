namespace Damebooru.Core.Interfaces;

public enum JobMode
{
    Missing,
    All,
}

public interface IJobReporter
{
    void Update(JobState state);
    void SetActivity(string? activityText);
    void SetProgress(int? current, int? total);
    void ClearProgress();
    void SetFinalText(string? finalText);
    void Flush();
}

public sealed class NullJobReporter : IJobReporter
{
    public static readonly NullJobReporter Instance = new();

    private NullJobReporter() { }

    public void Update(JobState state) { }
    public void SetActivity(string? activityText) { }
    public void SetProgress(int? current, int? total) { }
    public void ClearProgress() { }
    public void SetFinalText(string? finalText) { }
    public void Flush() { }
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
    Task ExecuteAsync(JobContext context);
}
