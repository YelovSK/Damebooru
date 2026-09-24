using System.Net;
using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

/// <summary>
/// A failure talking to an external provider. It is retried later unless the provider rejected the image itself.
/// </summary>
public sealed class ExternalProviderException : Exception
{
    public ExternalProviderException(
        AutoTagProvider provider,
        string message,
        bool rejectsImage = false,
        TimeSpan? retryAfter = null,
        bool stopCurrentRun = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        RejectsImage = rejectsImage;
        RetryAfter = retryAfter;
        StopCurrentRun = stopCurrentRun;
    }

    public AutoTagProvider Provider { get; }

    /// <summary>
    /// The provider refused this image (too small, unreadable, unsupported). Retrying cannot help until the content changes.
    /// </summary>
    public bool RejectsImage { get; }

    public TimeSpan? RetryAfter { get; }
    public bool StopCurrentRun { get; }

    /// <summary>
    /// An unsuccessful HTTP response. Authentication failures stop the whole run, since every post would fail the same way.
    /// </summary>
    public static ExternalProviderException ForHttpStatus(AutoTagProvider provider, string message, HttpStatusCode statusCode)
        => new(
            provider,
            message,
            stopCurrentRun: statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
}
