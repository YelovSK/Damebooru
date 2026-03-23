using Damebooru.Core.Entities;

namespace Damebooru.Processing.Infrastructure.External.Shared;

internal sealed class ImageUploadPreparationOptions
{
    public required string ProviderName { get; init; }
    public required AutoTagProvider Provider { get; init; }
    public required long MaxUploadBytes { get; init; }
    public int? MaxDimension { get; init; }
    public required ISet<string> SupportedUploadContentTypes { get; init; }
    public required ISet<string> SupportedUploadExtensions { get; init; }
}
