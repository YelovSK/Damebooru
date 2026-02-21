using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

using Damebooru.Processing.Scanning;
using Damebooru.Processing.Services;
using Damebooru.Processing.Pipeline;

namespace Damebooru.Processing;

public static class ServiceCollectionExtensions
{
    public sealed class ProcessingOptions
    {
        public bool EnableScheduler { get; set; } = true;
    }

    public static IServiceCollection AddDamebooruProcessing(
        this IServiceCollection services,
        DamebooruConfig config,
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
        services.AddSingleton<IFileIdentityResolver, PlatformFileIdentityResolver>();

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
