using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Damebooru.Processing.Infrastructure.External.SauceNao;

internal sealed class SauceNaoRateCoordinator
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SafetyPadding = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Queue<DateTimeOffset> _recentAttemptTimesUtc = new();

    private int? _shortLimit;
    private DateTimeOffset? _blockedUntilUtc;

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);

        try
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredAttempts(now);

            while (true)
            {
                var waitUntil = GetWaitUntil(now);
                if (waitUntil == null)
                {
                    return new Releaser(_requestGate);
                }

                var delay = waitUntil.Value - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                now = DateTimeOffset.UtcNow;
                PruneExpiredAttempts(now);
            }
        }
        catch
        {
            _requestGate.Release();
            throw;
        }
    }

    public SauceNaoRateObservation ObserveSuccess(SauceNaoHeaderDto header)
    {
        var now = DateTimeOffset.UtcNow;
        RecordCompletedAttempt(now);
        PruneExpiredAttempts(now);

        if (TryParseInt(header.ShortLimit, out var shortLimit) && shortLimit > 0)
        {
            _shortLimit = shortLimit;
        }

        if (_blockedUntilUtc.HasValue && _blockedUntilUtc <= now)
        {
            _blockedUntilUtc = null;
        }

        if (_shortLimit is not > 0 || header.ShortRemaining is not int shortRemaining || shortRemaining < 0)
        {
            return SauceNaoRateObservation.None;
        }

        var expectedRemaining = Math.Max(0, _shortLimit.Value - _recentAttemptTimesUtc.Count);
        if (shortRemaining < expectedRemaining)
        {
            ResyncShortWindow(now);
            return new SauceNaoRateObservation(
                RequiresResync: true,
                ExpectedShortRemaining: expectedRemaining,
                ActualShortRemaining: shortRemaining,
                BlockedUntilUtc: _blockedUntilUtc);
        }

        return SauceNaoRateObservation.None with
        {
            ExpectedShortRemaining = expectedRemaining,
            ActualShortRemaining = shortRemaining,
        };
    }

    public DateTimeOffset ObserveShortLimitExceeded()
    {
        var now = DateTimeOffset.UtcNow;
        ResyncShortWindow(now);
        return _blockedUntilUtc ?? (now + ShortWindow + SafetyPadding);
    }

    public void ObserveFailure()
    {
        var now = DateTimeOffset.UtcNow;
        RecordCompletedAttempt(now);
        PruneExpiredAttempts(now);
    }

    private DateTimeOffset? GetWaitUntil(DateTimeOffset now)
    {
        if (_blockedUntilUtc is { } blockedUntil && blockedUntil > now)
        {
            return blockedUntil;
        }

        if (_shortLimit is not > 0 || _recentAttemptTimesUtc.Count < _shortLimit)
        {
            return null;
        }

        var oldest = _recentAttemptTimesUtc.Peek();
        return oldest + ShortWindow + SafetyPadding;
    }

    private void ResyncShortWindow(DateTimeOffset now)
    {
        _recentAttemptTimesUtc.Clear();
        _blockedUntilUtc = now + ShortWindow + SafetyPadding;
    }

    private void PruneExpiredAttempts(DateTimeOffset now)
    {
        var cutoff = now - ShortWindow;
        while (_recentAttemptTimesUtc.Count > 0 && _recentAttemptTimesUtc.Peek() <= cutoff)
        {
            _recentAttemptTimesUtc.Dequeue();
        }
    }

    private void RecordCompletedAttempt(DateTimeOffset now)
        => _recentAttemptTimesUtc.Enqueue(now);

    private static bool TryParseInt(string? raw, out int value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private sealed class Releaser(SemaphoreSlim requestGate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            requestGate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record SauceNaoRateObservation(
    bool RequiresResync,
    int? ExpectedShortRemaining,
    int? ActualShortRemaining,
    DateTimeOffset? BlockedUntilUtc)
{
    public static readonly SauceNaoRateObservation None = new(false, null, null, null);
}
