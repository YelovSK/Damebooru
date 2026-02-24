using Damebooru.Core.Entities;
using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Damebooru.Processing.Services;

public class JobService : IJobService
{
    private static readonly TimeSpan StatePersistenceInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, JobInfo> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
    private readonly ConcurrentDictionary<JobKey, byte> _runningJobKeys = new();
    private readonly ILogger<JobService> _logger;
    private readonly IDbContextFactory<DamebooruDbContext> _dbContextFactory;
    private readonly Dictionary<JobKey, IJob> _registeredJobsByKey;
    private readonly TimeSpan _jobProgressReportInterval;

    public JobService(
        ILogger<JobService> logger, 
        IDbContextFactory<DamebooruDbContext> dbContextFactory,
        IEnumerable<IJob> jobs,
        IOptions<DamebooruConfig> config)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _jobProgressReportInterval = TimeSpan.FromMilliseconds(Math.Max(0, config.Value.Processing.JobProgressReportIntervalMs));
        _registeredJobsByKey = jobs
            .GroupBy(j => j.Key)
            .ToDictionary(g => g.Key, g => g.First());
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
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        
        var query = dbContext.JobExecutions.AsNoTracking().OrderByDescending(j => j.StartTime);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<string> StartJobAsync(JobKey jobKey, CancellationToken cancellationToken)
        => StartJobAsync(jobKey, cancellationToken, JobMode.Missing);

    public Task<string> StartJobAsync(JobKey jobKey, CancellationToken cancellationToken, JobMode mode)
    {
        if (!_registeredJobsByKey.TryGetValue(jobKey, out var job))
        {
            throw new ArgumentException($"Job '{jobKey.Value}' not found.", nameof(jobKey));
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

    public Task<string> StartJobAsync(JobKey jobKey, Func<CancellationToken, Task> action)
    {
        return StartJobInternalAsync(jobKey, jobKey.Value, ctx => action(ctx.CancellationToken));
    }

    private async Task<string> StartJobInternalAsync(JobKey jobKey, string jobName, Func<JobContext, Task> execute)
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
            await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None))
            {
                execution = new JobExecution
                {
                    JobKey = jobKey.Value,
                    JobName = jobName,
                    Status = JobStatus.Running,
                    StartTime = DateTime.UtcNow,
                    ActivityText = "Starting..."
                };
                dbContext.JobExecutions.Add(execution);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            var jobInfo = new JobInfo
            {
                Id = jobId,
                ExecutionId = execution.Id,
                Key = jobKey,
                Name = jobName,
                Status = JobStatus.Running,
                StartTime = execution.StartTime,
                State = new JobState
                {
                    ActivityText = "Starting..."
                }
            };

            _activeJobs[jobId] = jobInfo;
            _cancellationSources[jobId] = cts;

            _logger.LogInformation("Job started: {Name} ({Id}) - DB ID: {DbId}", jobName, jobId, execution.Id);

            _ = Task.Run(async () =>
            {
                var reporter = new JobReporter(
                    initialState: jobInfo.State,
                    minInterval: _jobProgressReportInterval,
                    onPublish: state => jobInfo.State = CloneState(state));

                using var statePersistenceCts = new CancellationTokenSource();
                var statePersistenceTask = PersistStateLoopAsync(
                    execution.Id,
                    reporter.GetSnapshot,
                    statePersistenceCts.Token);

                try
                {
                    var context = new JobContext
                    {
                        JobId = jobId,
                        CancellationToken = cts.Token,
                        Reporter = reporter,
                    };

                    await execute(context);

                    jobInfo.Status = JobStatus.Completed;
                    var finalState = reporter.GetSnapshot();
                    finalState.ActivityText ??= "Completed";
                    if (string.IsNullOrWhiteSpace(finalState.FinalText))
                    {
                        finalState.FinalText = "Completed successfully.";
                    }
                    reporter.Update(finalState);
                    reporter.Flush();

                    await UpdateJobStatusAsync(execution.Id, JobStatus.Completed, state: reporter.GetSnapshot());
                }
                catch (OperationCanceledException)
                {
                    jobInfo.Status = JobStatus.Cancelled;
                    var finalState = reporter.GetSnapshot();
                    finalState.ActivityText ??= "Cancelled";
                    finalState.FinalText ??= "Cancelled by user.";
                    reporter.Update(finalState);
                    reporter.Flush();

                    await UpdateJobStatusAsync(execution.Id, JobStatus.Cancelled, state: reporter.GetSnapshot());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job failed: {Id}", jobId);
                    jobInfo.Status = JobStatus.Failed;
                    var finalState = reporter.GetSnapshot();
                    finalState.ActivityText ??= "Failed";
                    finalState.FinalText = ex.Message;
                    reporter.Update(finalState);
                    reporter.Flush();

                    await UpdateJobStatusAsync(execution.Id, JobStatus.Failed, ex.Message, reporter.GetSnapshot());
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

    private static JobState CloneState(JobState state)
    {
        return new JobState
        {
            ActivityText = state.ActivityText,
            FinalText = state.FinalText,
            ProgressCurrent = state.ProgressCurrent,
            ProgressTotal = state.ProgressTotal,
        };
    }

    private async Task PersistStateLoopAsync(
        int executionId,
        Func<JobState> getLatestState,
        CancellationToken cancellationToken)
    {
        JobState? lastPersistedState = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(StatePersistenceInterval, cancellationToken);

            var state = getLatestState();

            if (JobStateEquals(lastPersistedState, state))
            {
                continue;
            }

            await UpdateJobStateDataAsync(executionId, state, cancellationToken);
            lastPersistedState = CloneState(state);
        }
    }

    private async Task UpdateJobStateDataAsync(int executionId, JobState state, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var execution = await dbContext.JobExecutions.FindAsync(new object[] { executionId }, cancellationToken);
            if (execution == null)
            {
                return;
            }

            ApplyStateToExecution(execution, state);

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

    private async Task UpdateJobStatusAsync(int executionId, JobStatus status, string? error = null, JobState? state = null)
    {
        try 
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            
            var execution = await dbContext.JobExecutions.FindAsync(executionId);
            if (execution != null)
            {
                execution.Status = status;
                execution.EndTime = DateTime.UtcNow;
                if (error != null) execution.ErrorMessage = error;
                if (state != null)
                {
                    ApplyStateToExecution(execution, state);
                }
                
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

    private static void ApplyStateToExecution(JobExecution execution, JobState state)
    {
        execution.ActivityText = NormalizeText(state.ActivityText);
        execution.FinalText = NormalizeText(state.FinalText);
        execution.ProgressCurrent = state.ProgressCurrent;
        execution.ProgressTotal = state.ProgressTotal;
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static bool JobStateEquals(JobState? left, JobState? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return string.Equals(left.ActivityText, right.ActivityText, StringComparison.Ordinal)
            && string.Equals(left.FinalText, right.FinalText, StringComparison.Ordinal)
            && left.ProgressCurrent == right.ProgressCurrent
            && left.ProgressTotal == right.ProgressTotal;
    }

}
