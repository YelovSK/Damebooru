using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Damebooru.Processing.Jobs;

public class ScanAllLibrariesJob : IScheduledJob
{
    public static readonly JobKey JobKey = JobKeys.ScanAllLibraries;
    public const string JobName = "Scan All Libraries";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PostEnrichmentPasses _enrichmentPasses;

    public ScanAllLibrariesJob(IServiceScopeFactory scopeFactory, PostEnrichmentPasses enrichmentPasses)
    {
        _scopeFactory = scopeFactory;
        _enrichmentPasses = enrichmentPasses;
    }

    public int DisplayOrder => 10;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Scans all libraries, then extracts metadata, similarity hashes and thumbnails for posts missing them.";
    public bool SupportsAllMode => false;
    public string DefaultCron => "0 */6 * * *";

    public async Task<string> ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var syncService = scope.ServiceProvider.GetRequiredService<ILibrarySyncProcessor>();

        var libraries = await dbContext.Libraries.ToListAsync(context.CancellationToken);
        if (libraries.Count == 0)
        {
            return "No libraries configured.";
        }

        var result = ScanResult.Empty;
        foreach (var library in libraries)
        {
            result += await syncService.ProcessDirectoryAsync(library, library.Path, context.Reporter, context.CancellationToken);
        }

        return $"{result.Summary} {await _enrichmentPasses.RunMissingAsync(context)}";
    }
}
