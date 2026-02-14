using Bakabooru.Core;
using Bakabooru.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Bakabooru.Processing.Steps;

public class MetadataStep : IMediaProcessingStep
{
    private readonly IMediaFileProcessor _mediaFileProcessor;
    private readonly ILogger<MetadataStep> _logger;

    public int Order => 20;

    public MetadataStep(IMediaFileProcessor mediaFileProcessor, ILogger<MetadataStep> logger)
    {
        _mediaFileProcessor = mediaFileProcessor;
        _logger = logger;
    }

    public async Task ExecuteAsync(MediaProcessingContext context, CancellationToken cancellationToken)
    {
        var metadata = await _mediaFileProcessor.GetMetadataAsync(context.FilePath, cancellationToken);
        context.Width = metadata.Width;
        context.Height = metadata.Height;
        context.ContentType = metadata.ContentType ?? SupportedMedia.GetMimeType(Path.GetExtension(context.FilePath));
    }
}
