using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Logging;

public sealed class AppLogWriterService : BackgroundService
{
    private readonly AppLogChannel _channel;
    private readonly IDbContextFactory<DamebooruDbContext> _dbContextFactory;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    public AppLogWriterService(
        AppLogChannel channel,
        IDbContextFactory<DamebooruDbContext> dbContextFactory,
        IOptions<DamebooruConfig> options)
    {
        _channel = channel;
        _dbContextFactory = dbContextFactory;
        _batchSize = Math.Clamp(options.Value.Logging.Db.BatchSize, 1, 2000);
        _flushInterval = TimeSpan.FromMilliseconds(Math.Clamp(options.Value.Logging.Db.FlushIntervalMs, 100, 10000));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AppLogWriteEntry>(_batchSize);
        var lastFlushUtc = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromMilliseconds(100);

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_channel.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
                if (batch.Count >= _batchSize)
                {
                    await TryFlushAsync(batch, stoppingToken);
                    lastFlushUtc = DateTime.UtcNow;
                }
            }

            if (batch.Count > 0 && DateTime.UtcNow - lastFlushUtc >= _flushInterval)
            {
                await TryFlushAsync(batch, stoppingToken);
                lastFlushUtc = DateTime.UtcNow;
            }

            await Task.Delay(pollInterval, stoppingToken);
        }

        if (batch.Count > 0)
        {
            await TryFlushAsync(batch, CancellationToken.None);
        }
    }

    private async Task TryFlushAsync(List<AppLogWriteEntry> batch, CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(batch, cancellationToken);
        }
        catch
        {
            // Avoid crashing host due to log persistence failures.
            batch.Clear();
        }
    }

    private async Task FlushAsync(List<AppLogWriteEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var entries = batch
            .Select(e => new AppLogEntry
            {
                TimestampUtc = e.TimestampUtc,
                Level = e.Level,
                Category = e.Category,
                Message = e.Message,
                Exception = e.Exception,
                MessageTemplate = e.MessageTemplate,
                PropertiesJson = e.PropertiesJson,
            })
            .ToList();

        batch.Clear();

        using var _ = AppLogCaptureContext.BeginSuppressed();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AppLogEntries.AddRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
