using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Damebooru.Processing.Jobs;

public class ScanAllLibrariesJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.ScanAllLibraries;
    public const string JobName = "Scan All Libraries";

    private readonly IServiceScopeFactory _scopeFactory;

    public ScanAllLibrariesJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public int DisplayOrder => 10;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Triggers a recursive scan for all configured libraries.";
    public bool SupportsAllMode => false;

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

        return result.Summary;
    }
}
