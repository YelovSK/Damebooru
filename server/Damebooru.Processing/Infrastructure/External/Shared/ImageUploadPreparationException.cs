namespace Damebooru.Processing.Infrastructure.External.Shared;

internal sealed class ImageUploadPreparationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
