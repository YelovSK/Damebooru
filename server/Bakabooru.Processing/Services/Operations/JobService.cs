using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Bakabooru.Processing.Services;

public class JobService : IJobService
{
    private static readonly TimeSpan StatePersistenceInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, JobInfo> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
    private readonly ConcurrentDictionary<string, byte> _runningJobKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<JobService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<string, IJob> _registeredJobsByKey;
    private readonly Dictionary<string, IJob> _registeredJobsByName;

    public JobService(
        ILogger<JobService> logger, 
        IServiceScopeFactory scopeFactory,
        IEnumerable<IJob> jobs)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _registeredJobsByKey = jobs
            .GroupBy(j => j.Key)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _registeredJobsByName = jobs
            .GroupBy(j => j.Name)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<JobInfo> GetActiveJobs() => _activeJobs.Values.ToList();

    public IEnumerable<JobDefinition> GetAvailableJobs()
    {
        return _registeredJobsByKey.Values
            .OrderBy(j => j.DisplayOrder)
            .ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .Select(j => new JobDefinition
            {
                Key = j.Key,
                Name = j.Name,
                Description = j.Description,
                SupportsAllMode = j.SupportsAllMode
            });
    }

    public async Task<(List<JobExecution> Items, int Total)> GetJobHistoryAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
        
        var query = dbContext.JobExecutions.AsNoTracking().OrderByDescending(j => j.StartTime);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<string> StartJobAsync(string jobName, CancellationToken cancellationToken)
        => StartJobAsync(jobName, cancellationToken, JobMode.Missing);

    public Task<string> StartJobAsync(string jobName, CancellationToken cancellationToken, JobMode mode)
    {
        if (!TryResolveJob(jobName, out var job))
        {
            throw new ArgumentException($"Job '{jobName}' not found.", nameof(jobName));
        }

        if (mode == JobMode.All && !job.SupportsAllMode)
        {
            throw new InvalidOperationException($"Job '{job.Name}' does not support mode 'all'.");
        }

        return StartJobInternalAsync(job.Key, job.Name, ctx =>
        {
            ctx.Mode = mode;
            return job.ExecuteAsync(ctx);
        });
    }

    public Task<string> StartJobAsync(string jobName, Func<CancellationToken, Task> action)
    {
        return StartJobInternalAsync(jobName, jobName, ctx => action(ctx.CancellationToken));
    }

    private bool TryResolveJob(string keyOrName, out IJob job)
    {
        if (_registeredJobsByKey.TryGetValue(keyOrName, out job!))
        {
            return true;
        }

        return _registeredJobsByName.TryGetValue(keyOrName, out job!);
    }

    private async Task<string> StartJobInternalAsync(string jobKey, string jobName, Func<JobContext, Task> execute)
    {
        if (!_runningJobKeys.TryAdd(jobKey, 0))
        {
            throw new InvalidOperationException($"Job '{jobName}' is already running.");
        }

        var jobId = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();

        try
        {
            // Create DB entry
            JobExecution execution;
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
                execution = new JobExecution
                {
                    JobName = jobName,
                    Status = JobStatus.Running,
                    StartTime = DateTime.UtcNow
                };
                dbContext.JobExecutions.Add(execution);
                await dbContext.SaveChangesAsync();
            }

            var jobInfo = new JobInfo
            {
                Id = jobId,
                ExecutionId = execution.Id,
                Name = jobName,
                Status = JobStatus.Running,
                StartTime = execution.StartTime,
                State = new JobState
                {
                    Phase = "Starting..."
                }
            };

            _activeJobs[jobId] = jobInfo;
            _cancellationSources[jobId] = cts;

            _logger.LogInformation("Job started: {Name} ({Id}) - DB ID: {DbId}", jobName, jobId, execution.Id);

            _ = Task.Run(async () =>
            {
                JobState latestState = CloneState(jobInfo.State);
                string latestStateData = JobStateSerialization.Serialize(latestState);
                var stateLock = new object();

                JobState GetLatestState()
                {
                    lock (stateLock)
                    {
                        return CloneState(latestState);
                    }
                }

                string GetLatestStateData()
                {
                    lock (stateLock)
                    {
                        return latestStateData;
                    }
                }

                void SetLatestState(JobState state)
                {
                    lock (stateLock)
                    {
                        latestState = MergeState(latestState, state);
                        latestStateData = JobStateSerialization.Serialize(latestState);
                        jobInfo.State = CloneState(latestState);
                    }
                }

                using var statePersistenceCts = new CancellationTokenSource();
                var statePersistenceTask = PersistStateLoopAsync(
                    execution.Id,
                    GetLatestStateData,
                    statePersistenceCts.Token);

                try
                {
                    var context = new JobContext
                    {
                        JobId = jobId,
                        CancellationToken = cts.Token,
                        State = new InlineProgress<JobState>(state =>
                        {
                            if (state == null)
                            {
                                return;
                            }

                            SetLatestState(state);
                        })
                    };

                    await execute(context);

                    jobInfo.Status = JobStatus.Completed;
                    var finalState = GetLatestState();
                    if (string.IsNullOrWhiteSpace(finalState.Phase))
                    {
                        finalState.Phase = "Completed";
                    }
                    finalState.Summary ??= "Completed successfully.";
                    SetLatestState(finalState);

                    await UpdateJobStatusAsync(execution.Id, JobStatus.Completed, stateData: GetLatestStateData());
                }
                catch (OperationCanceledException)
                {
                    jobInfo.Status = JobStatus.Cancelled;
                    var finalState = GetLatestState();
                    finalState.Phase = "Cancelled";
                    finalState.Summary ??= "Cancelled by user.";
                    SetLatestState(finalState);

                    await UpdateJobStatusAsync(execution.Id, JobStatus.Cancelled, stateData: GetLatestStateData());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job failed: {Id}", jobId);
                    jobInfo.Status = JobStatus.Failed;
                    var finalState = GetLatestState();
                    finalState.Phase = "Failed";
                    finalState.Summary = ex.Message;
                    SetLatestState(finalState);

                    await UpdateJobStatusAsync(execution.Id, JobStatus.Failed, ex.Message, GetLatestStateData());
                }
                finally
                {
                    statePersistenceCts.Cancel();
                    try
                    {
                        await statePersistenceTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on shutdown/cancel.
                    }

                    jobInfo.EndTime = DateTime.UtcNow;
                    if (_cancellationSources.TryRemove(jobId, out var removedCts))
                    {
                        removedCts.Dispose();
                    }

                    _runningJobKeys.TryRemove(jobKey, out _);
                    _ = RemoveJobWithDelay(jobId);
                }
            });

            return jobId;
        }
        catch
        {
            _runningJobKeys.TryRemove(jobKey, out _);
            cts.Dispose();
            throw;
        }
    }

    private async Task RemoveJobWithDelay(string jobId)
    {
        await Task.Delay(TimeSpan.FromSeconds(30)); 
        _activeJobs.TryRemove(jobId, out _);
    }

    private static JobState MergeState(JobState current, JobState update)
    {
        var phase = string.IsNullOrWhiteSpace(update.Phase)
            ? current.Phase
            : update.Phase.Trim();

        if (string.IsNullOrWhiteSpace(phase))
        {
            phase = "Running...";
        }

        string? summary;
        if (update.Summary is null)
        {
            summary = current.Summary;
        }
        else
        {
            summary = string.IsNullOrWhiteSpace(update.Summary) ? null : update.Summary.Trim();
        }

        return new JobState
        {
            Phase = phase,
            Processed = update.Processed ?? current.Processed,
            Total = update.Total ?? current.Total,
            Succeeded = update.Succeeded ?? current.Succeeded,
            Failed = update.Failed ?? current.Failed,
            Skipped = update.Skipped ?? current.Skipped,
            Summary = summary
        };
    }

    private static JobState CloneState(JobState state)
    {
        return new JobState
        {
            Phase = state.Phase,
            Processed = state.Processed,
            Total = state.Total,
            Succeeded = state.Succeeded,
            Failed = state.Failed,
            Skipped = state.Skipped,
            Summary = state.Summary
        };
    }

    private async Task PersistStateLoopAsync(
        int executionId,
        Func<string> getLatestStateData,
        CancellationToken cancellationToken)
    {
        string? lastPersistedStateData = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(StatePersistenceInterval, cancellationToken);

            var stateData = getLatestStateData();

            if (string.Equals(lastPersistedStateData, stateData, StringComparison.Ordinal))
            {
                continue;
            }

            await UpdateJobStateDataAsync(executionId, stateData, cancellationToken);
            lastPersistedStateData = stateData;
        }
    }

    private async Task UpdateJobStateDataAsync(int executionId, string stateData, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
            var execution = await dbContext.JobExecutions.FindAsync(new object[] { executionId }, cancellationToken);
            if (execution == null)
            {
                return;
            }

            execution.ResultData = stateData;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore cancellation in periodic persistence loop.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist live job state for DB ID {Id}", executionId);
        }
    }

    private async Task UpdateJobStatusAsync(int executionId, JobStatus status, string? error = null, string? stateData = null)
    {
        try 
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
            
            var execution = await dbContext.JobExecutions.FindAsync(executionId);
            if (execution != null)
            {
                execution.Status = status;
                execution.EndTime = DateTime.UtcNow;
                if (error != null) execution.ErrorMessage = error;
                if (!string.IsNullOrWhiteSpace(stateData)) execution.ResultData = stateData;
                
                await dbContext.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update job status for DB ID {Id}", executionId);
        }
    }

    public void CancelJob(string jobId)
    {
        if (_cancellationSources.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onReport;

        public InlineProgress(Action<T> onReport)
        {
            _onReport = onReport;
        }

        public void Report(T value)
        {
            _onReport(value);
        }
    }
}
