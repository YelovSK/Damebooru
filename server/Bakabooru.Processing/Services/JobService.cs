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
    private readonly ConcurrentDictionary<string, JobInfo> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
    private readonly ILogger<JobService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<string, IJob> _registeredJobs;

    public JobService(
        ILogger<JobService> logger, 
        IServiceScopeFactory scopeFactory,
        IEnumerable<IJob> jobs)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _registeredJobs = jobs.GroupBy(j => j.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<JobInfo> GetActiveJobs() => _activeJobs.Values.ToList();

    public IEnumerable<string> GetAvailableJobs() => _registeredJobs.Keys;

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
        => StartJobAsync(jobName, cancellationToken, configure: null);

    public Task<string> StartJobAsync(string jobName, CancellationToken cancellationToken, Action<IJob>? configure, JobMode mode = JobMode.Missing)
    {
        if (!_registeredJobs.TryGetValue(jobName, out var job))
        {
            throw new ArgumentException($"Job '{jobName}' not found.", nameof(jobName));
        }

        configure?.Invoke(job);

        return StartJobInternalAsync(jobName, ctx =>
        {
            ctx.Mode = mode;
            return job.ExecuteAsync(ctx);
        });
    }

    public Task<string> StartJobAsync(string jobName, Func<CancellationToken, Task> action)
    {
        return StartJobInternalAsync(jobName, ctx => action(ctx.CancellationToken));
    }

    private async Task<string> StartJobInternalAsync(string jobName, Func<JobContext, Task> execute)
    {
        var jobId = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();
        
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
            Name = jobName,
            Status = JobStatus.Running,
            StartTime = execution.StartTime,
            Progress = 0,
            Message = "Starting..."
        };

        _activeJobs[jobId] = jobInfo;
        _cancellationSources[jobId] = cts;

        _logger.LogInformation("Job started: {Name} ({Id}) - DB ID: {DbId}", jobName, jobId, execution.Id);

        _ = Task.Run(async () =>
        {
            try
            {
                var context = new JobContext
                {
                    JobId = jobId,
                    CancellationToken = cts.Token,
                    Progress = new Progress<float>(percent => {
                        // Accept both 0..1 and 0..100 progress reporting styles.
                        var normalized = percent <= 1f ? percent * 100f : percent;
                        jobInfo.Progress = Math.Clamp(normalized, 0f, 100f);
                    }),
                    Status = new Progress<string>(message => {
                        jobInfo.Message = message;
                    })
                };

                await execute(context);
                
                jobInfo.Status = JobStatus.Completed;
                jobInfo.Message = "Completed successfully";
                jobInfo.Progress = 100;
                
                await UpdateJobStatusAsync(execution.Id, JobStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                jobInfo.Status = JobStatus.Cancelled;
                jobInfo.Message = "Cancelled by user";
                
                await UpdateJobStatusAsync(execution.Id, JobStatus.Cancelled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job failed: {Id}", jobId);
                jobInfo.Status = JobStatus.Failed;
                jobInfo.Message = ex.Message;
                
                await UpdateJobStatusAsync(execution.Id, JobStatus.Failed, ex.Message);
            }
            finally
            {
                jobInfo.EndTime = DateTime.UtcNow;
                if (_cancellationSources.TryRemove(jobId, out var removedCts))
                {
                    removedCts.Dispose();
                }
                _ = RemoveJobWithDelay(jobId);
            }
        });

        return jobId;
    }

    private async Task RemoveJobWithDelay(string jobId)
    {
        await Task.Delay(TimeSpan.FromSeconds(30)); 
        _activeJobs.TryRemove(jobId, out _);
    }

    private async Task UpdateJobStatusAsync(int executionId, JobStatus status, string? error = null)
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
}
