namespace Damebooru.Processing.Infrastructure.External.Shared;

internal sealed class ImageUploadPreparationOptions
{
    public long? MaxUploadBytes { get; init; }
    public int? MaxDimension { get; init; }
    public required ISet<string> SupportedUploadContentTypes { get; init; }
    public required ISet<string> SupportedUploadExtensions { get; init; }
}
