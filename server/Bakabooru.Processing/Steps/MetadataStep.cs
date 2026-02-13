using Bakabooru.Core;
using Bakabooru.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Bakabooru.Processing.Steps;

public class MetadataStep : IMediaProcessingStep
{
    private readonly IImageProcessor _mediaProcessor;
    private readonly ILogger<MetadataStep> _logger;

    public int Order => 20;

    public MetadataStep(IImageProcessor mediaProcessor, ILogger<MetadataStep> logger)
    {
        _mediaProcessor = mediaProcessor;
        _logger = logger;
    }

    public async Task ExecuteAsync(MediaProcessingContext context, CancellationToken cancellationToken)
    {
        var metadata = await _mediaProcessor.GetMetadataAsync(context.FilePath, cancellationToken);
        context.Width = metadata.Width;
        context.Height = metadata.Height;
        context.ContentType = metadata.ContentType ?? SupportedMedia.GetMimeType(Path.GetExtension(context.FilePath));
    }
}
