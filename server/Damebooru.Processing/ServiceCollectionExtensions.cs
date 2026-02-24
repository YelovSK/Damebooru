using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Processing.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using PhotoSauce.MagicScaler;
using PhotoSauce.NativeCodecs.Giflib;
using PhotoSauce.NativeCodecs.Libjpeg;
using PhotoSauce.NativeCodecs.Libjxl;
using PhotoSauce.NativeCodecs.Libpng;
using PhotoSauce.NativeCodecs.Libwebp;

using Damebooru.Processing.Scanning;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.Scanning;

namespace Damebooru.Processing;

public static class ServiceCollectionExtensions
{
    private static readonly object CodecRegistrationLock = new();
    private static bool _codecsRegistered;

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

        RegisterImageCodecs();

        // Infrastructure
        services.AddSingleton<IHasherService, ContentHasher>();
        services.AddSingleton<IMediaFileProcessor, FFmpegProcessor>();
        services.AddSingleton<ISimilarityService, ImageHashService>();
        services.AddSingleton<IFileIdentityResolver, PlatformFileIdentityResolver>();

        // Core Pipeline Services
        services.AddSingleton<ILibrarySyncProcessor, LibrarySyncService>();
        
        services.AddSingleton<ChannelPostIngestionService>();
        services.AddSingleton<IPostIngestionService>(sp => sp.GetRequiredService<ChannelPostIngestionService>());
        services.AddHostedService(sp => sp.GetRequiredService<ChannelPostIngestionService>());
        
        services.AddSingleton<IJobService, JobService>();
        if (options.EnableScheduler)
        {
            services.AddHostedService<SchedulerService>();
        }

        services.AddSingleton<IMediaSource, FileSystemMediaSource>();
        services.AddTransient<FolderTaggingService>();

        // Jobs
        services.AddTransient<IJob, Jobs.ScanAllLibrariesJob>();
        services.AddTransient<IJob, Jobs.FindDuplicatesJob>();
        services.AddTransient<IJob, Jobs.GenerateThumbnailsJob>();
        services.AddTransient<IJob, Jobs.CleanupOrphanedThumbnailsJob>();
        services.AddTransient<IJob, Jobs.CleanupInvalidExclusionsJob>();
        services.AddTransient<IJob, Jobs.ExtractMetadataJob>();
        services.AddTransient<IJob, Jobs.ComputeSimilarityJob>();
        services.AddTransient<IJob, Jobs.ApplyFolderTagsJob>();
        services.AddTransient<IJob, Jobs.SanitizeTagNamesJob>();

        return services;
    }

    private static void RegisterImageCodecs()
    {
        lock (CodecRegistrationLock)
        {
            if (_codecsRegistered)
            {
                return;
            }

            CodecManager.Configure(codecs =>
            {
                codecs.UseLibjpeg();
                codecs.UseLibpng();
                codecs.UseGiflib();
                codecs.UseLibwebp();
                codecs.UseLibjxl();
            });

            _codecsRegistered = true;
        }
    }
}
