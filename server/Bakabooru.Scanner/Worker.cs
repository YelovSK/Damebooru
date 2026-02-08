using Bakabooru.Core.Interfaces;

namespace Bakabooru.Scanner;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IScannerService _scannerService;
    private readonly TimeSpan _scanInterval = TimeSpan.FromHours(1); // Default global interval

    public Worker(ILogger<Worker> logger, IScannerService scannerService)
    {
        _logger = logger;
        _scannerService = scannerService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let app start
        await Task.Delay(1000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Worker starting scan at: {time}", DateTimeOffset.Now);
                await _scannerService.ScanAllLibrariesAsync(stoppingToken);
                _logger.LogInformation("Worker completed scan at: {time}", DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scan");
            }

            // Wait for next scan cycle
            await Task.Delay(_scanInterval, stoppingToken);
        }
    }
}
