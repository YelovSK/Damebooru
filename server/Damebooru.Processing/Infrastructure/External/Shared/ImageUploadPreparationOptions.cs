namespace Damebooru.Processing.Infrastructure.External.Shared;

internal sealed class ImageUploadPreparationOptions
{
    // Reverse image search matches against small fingerprints, so larger uploads only cost upload bandwidth.
    // Anything bigger is re-encoded to a JPEG no larger than MaxDimension.
    public long MaxUploadBytes { get; init; } = 3L * 1024 * 1024;
    public int MaxDimension { get; init; } = 2000;
    public required ISet<string> SupportedUploadContentTypes { get; init; }
    public required ISet<string> SupportedUploadExtensions { get; init; }
}
