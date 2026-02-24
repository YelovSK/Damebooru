using Damebooru.Core.Config;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Logging;

public sealed class AppLogRetentionService : BackgroundService
{
    private readonly IDbContextFactory<DamebooruDbContext> _dbContextFactory;
    private readonly int _retentionDays;
    private readonly int _maxRows;
    private readonly TimeSpan _checkInterval;

    public AppLogRetentionService(
        IDbContextFactory<DamebooruDbContext> dbContextFactory,
        IOptions<DamebooruConfig> options)
    {
        _dbContextFactory = dbContextFactory;
        _retentionDays = Math.Max(1, options.Value.Logging.Db.RetentionDays);
        _maxRows = Math.Max(1, options.Value.Logging.Db.MaxRows);
        _checkInterval = TimeSpan.FromMinutes(Math.Clamp(options.Value.Logging.Db.RetentionCheckIntervalMinutes, 1, 1440));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval);

        await EnforceRetentionAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EnforceRetentionAsync(stoppingToken);
            }
            catch
            {
                // Best-effort retention.
            }
        }
    }

    private async Task EnforceRetentionAsync(CancellationToken cancellationToken)
    {
        using var _ = AppLogCaptureContext.BeginSuppressed();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        await dbContext.AppLogEntries
            .Where(e => e.TimestampUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var total = await dbContext.AppLogEntries.CountAsync(cancellationToken);
        var overflow = total - _maxRows;
        while (overflow > 0)
        {
            var batchSize = Math.Min(overflow, 1000);
            var idsToDelete = await dbContext.AppLogEntries
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (idsToDelete.Count == 0)
            {
                break;
            }

            await dbContext.AppLogEntries
                .Where(e => idsToDelete.Contains(e.Id))
                .ExecuteDeleteAsync(cancellationToken);

            overflow -= idsToDelete.Count;
        }
    }
}
