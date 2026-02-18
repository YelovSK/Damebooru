using Bakabooru.Core.Config;
using Bakabooru.Core.Interfaces;
using Bakabooru.Processing.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

using Bakabooru.Processing.Scanning;
using Bakabooru.Processing.Services;
using Bakabooru.Processing.Pipeline;

namespace Bakabooru.Processing;

public static class ServiceCollectionExtensions
{
    public sealed class ProcessingOptions
    {
        public bool EnableScheduler { get; set; } = true;
    }

    public static IServiceCollection AddBakabooruProcessing(
        this IServiceCollection services,
        BakabooruConfig config,
        Action<ProcessingOptions>? configure = null)
    {
        var options = new ProcessingOptions
        {
            EnableScheduler = config.Processing.RunScheduler
        };
        configure?.Invoke(options);

        // Infrastructure
        services.AddSingleton<IHasherService, ContentHasher>();
        services.AddSingleton<IMediaFileProcessor, FFmpegProcessor>();
        services.AddSingleton<ISimilarityService, ImageHashService>();

        // Core Pipeline Services
        services.AddSingleton<ILibrarySyncProcessor, LibrarySyncProcessor>();
        
        services.AddSingleton<ChannelPostIngestionService>();
        services.AddSingleton<IPostIngestionService>(sp => sp.GetRequiredService<ChannelPostIngestionService>());
        services.AddHostedService(sp => sp.GetRequiredService<ChannelPostIngestionService>());
        
        services.AddSingleton<IJobService, JobService>();
        if (options.EnableScheduler)
        {
            services.AddHostedService<SchedulerService>();
        }

        services.AddSingleton<IMediaSource, FileSystemMediaSource>();
        services.AddTransient<IScannerService, RecursiveScanner>();
        services.AddTransient<FolderTaggingService>();

        // Jobs
        services.AddTransient<IJob, Jobs.ScanAllLibrariesJob>();
        services.AddTransient<IJob, Jobs.FindDuplicatesJob>();
        services.AddTransient<IJob, Jobs.GenerateThumbnailsJob>();
        services.AddTransient<IJob, Jobs.CleanupOrphanedThumbnailsJob>();
        services.AddTransient<IJob, Jobs.ExtractMetadataJob>();
        services.AddTransient<IJob, Jobs.ComputeSimilarityJob>();
        services.AddTransient<IJob, Jobs.ApplyFolderTagsJob>();
        services.AddTransient<IJob, Jobs.SanitizeTagNamesJob>();

        return services;
    }
}
