using Bakabooru.Core.Config;
using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace Bakabooru.Processing.Services;

public class ChannelPostIngestionService : IPostIngestionService, IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChannelPostIngestionService> _logger;
    private readonly Channel<Post> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _processingTask;
    private int _pendingWorkItems = 0;
    private readonly int _batchSize;

    public ChannelPostIngestionService(
        IServiceScopeFactory scopeFactory,
        ILogger<ChannelPostIngestionService> logger,
        IOptions<BakabooruConfig> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _batchSize = Math.Max(1, options.Value.Ingestion.BatchSize);
        var channelCapacity = Math.Max(10, options.Value.Ingestion.ChannelCapacity);
        
        // Bounded channel provides backpressure - if the DB is slow, the scanner will eventually pause at Enqueue
        _channel = Channel.CreateBounded<Post>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false 
        });
    }

    public async Task EnqueuePostAsync(Post post, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pendingWorkItems);
        try
        {
            await _channel.Writer.WriteAsync(post, cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingWorkItems);
            throw;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        // Wait until all pending items are processed
        // We poll here because we need to wait for the consumer to finish saving.
        // A more sophisticated approach would use a TaskCompletionSource signal, but this is simple and robust.
        while (Volatile.Read(ref _pendingWorkItems) > 0)
        {
            if (_processingTask?.IsFaulted == true)
            {
                throw new InvalidOperationException("Ingestion background task has failed.", _processingTask.Exception);
            }
            
            await Task.Delay(50, cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processingTask = Task.Run(ProcessQueueAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.Complete();
        await _shutdownCts.CancelAsync();
        
        if (_processingTask != null)
        {
            await Task.WhenAny(_processingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    private async Task ProcessQueueAsync()
    {
        _logger.LogInformation("Ingestion service started.");
        var batch = new List<Post>(_batchSize);

        try 
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdownCts.Token))
            {
                while (_channel.Reader.TryRead(out var post))
                {
                    batch.Add(post);
                    if (batch.Count >= _batchSize)
                    {
                        await SaveBatchAsync(batch);
                        batch.Clear();
                    }
                }

                // Channel is empty (for now), save whatever we have to keep latency low
                if (batch.Count > 0)
                {
                    await SaveBatchAsync(batch);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in ingestion processing loop.");
        }
        finally
        {
            // Process any remaining items if we are shutting down gracefully
            if (batch.Count > 0)
            {
                await SaveBatchAsync(batch);
            }
        }
    }

    private async Task SaveBatchAsync(List<Post> posts)
    {
        const int maxRetries = 1;
        
        try
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
                    
                    dbContext.Posts.AddRange(posts);
                    await dbContext.SaveChangesAsync();
                    _logger.LogDebug("Saved batch of {Count} posts.", posts.Count);
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Failed to save batch of {Count} posts (attempt {Attempt}), retrying...", 
                        posts.Count, attempt + 1);
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save batch of {Count} posts after {Attempts} attempts. Data lost.", 
                        posts.Count, maxRetries + 1);
                }
            }
        }
        finally
        {
            Interlocked.Add(ref _pendingWorkItems, -posts.Count);
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
