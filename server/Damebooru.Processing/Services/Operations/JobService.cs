using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    public JobService(
        ILogger<JobService> logger, 
        IDbContextFactory<DamebooruDbContext> dbContextFactory,
        IEnumerable<IJob> jobs)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
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

    public bool IsRunning(JobKey jobKey) => _runningJobKeys.ContainsKey(jobKey);

    public Task<string> StartJobAsync(JobKey jobKey, JobMode mode = JobMode.Missing)
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

    public Task<string> StartJobAsync(JobKey jobKey, Func<JobContext, Task<string>> execute)
        => StartJobInternalAsync(jobKey, jobKey.Value, execute);

    private async Task<string> StartJobInternalAsync(JobKey jobKey, string jobName, Func<JobContext, Task<string>> execute)
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
                using var statePersistenceCts = new CancellationTokenSource();
                var statePersistenceTask = PersistStateLoopAsync(execution.Id, jobInfo, statePersistenceCts.Token);

                JobStatus status;
                string finalText;
                string? error = null;
                try
                {
                    finalText = await execute(new JobContext
                    {
                        JobId = jobId,
                        CancellationToken = cts.Token,
                        Reporter = new JobReporter(jobInfo),
                    });
                    status = JobStatus.Completed;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    finalText = "Cancelled by user.";
                    status = JobStatus.Cancelled;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job failed: {Id}", jobId);
                    finalText = error = ex.Message;
                    status = JobStatus.Failed;
                }

                statePersistenceCts.Cancel();
                try
                {
                    await statePersistenceTask;
                }
                catch (OperationCanceledException)
                {
                }

                jobInfo.State = jobInfo.State with { ActivityText = null, FinalText = finalText };
                jobInfo.Status = status;
                jobInfo.EndTime = DateTime.UtcNow;
                await UpdateJobStatusAsync(execution.Id, status, error, jobInfo.State);

                if (_cancellationSources.TryRemove(jobId, out var removedCts))
                {
                    removedCts.Dispose();
                }

                _runningJobKeys.TryRemove(jobKey, out _);
                _ = RemoveJobWithDelay(jobId);
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

    private async Task PersistStateLoopAsync(int executionId, JobInfo jobInfo, CancellationToken cancellationToken)
    {
        JobState? lastPersistedState = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(StatePersistenceInterval, cancellationToken);

            var state = jobInfo.State;
            if (state == lastPersistedState)
            {
                continue;
            }

            await UpdateJobStateDataAsync(executionId, state, cancellationToken);
            lastPersistedState = state;
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

    private async Task UpdateJobStatusAsync(int executionId, JobStatus status, string? error, JobState state)
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
                ApplyStateToExecution(execution, state);
                
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

}
