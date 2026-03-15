using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

public sealed class ExternalProviderException : Exception
{
    public ExternalProviderException(
        AutoTagProvider provider,
        string message,
        bool isRetryable,
        TimeSpan? retryAfter = null,
        bool stopCurrentRun = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
        StopCurrentRun = stopCurrentRun;
    }

    public AutoTagProvider Provider { get; }
    public bool IsRetryable { get; }
    public TimeSpan? RetryAfter { get; }
    public bool StopCurrentRun { get; }
}
