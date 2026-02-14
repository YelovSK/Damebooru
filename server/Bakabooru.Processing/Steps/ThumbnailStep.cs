using Bakabooru.Core.Config;
using Bakabooru.Core.Interfaces;
using Bakabooru.Core.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bakabooru.Processing.Steps;

public class ThumbnailStep : IMediaProcessingStep
{
    private readonly IMediaFileProcessor _mediaFileProcessor;
    private readonly ILogger<ThumbnailStep> _logger;
    private readonly string _thumbnailPath;

    public int Order => 30;

    public ThumbnailStep(
        IMediaFileProcessor mediaFileProcessor,
        ILogger<ThumbnailStep> logger,
        IOptions<BakabooruConfig> options,
        IHostEnvironment hostEnvironment)
    {
        _mediaFileProcessor = mediaFileProcessor;
        _logger = logger;
        _thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
            hostEnvironment.ContentRootPath,
            options.Value.Storage.ThumbnailPath);
        
        if (!Directory.Exists(_thumbnailPath))
        {
            Directory.CreateDirectory(_thumbnailPath);
        }
    }

    public async Task ExecuteAsync(MediaProcessingContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.ContentHash)) return;

        var destination = MediaPaths.GetThumbnailFilePath(_thumbnailPath, context.ContentHash);
        if (!File.Exists(destination))
        {
            _logger.LogInformation("Generating thumbnail: {Path}", context.RelativePath);
            await _mediaFileProcessor.GenerateThumbnailAsync(context.FilePath, destination, 400, cancellationToken);
        }
        
        context.ThumbnailPath = destination;
    }
}
