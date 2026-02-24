namespace Damebooru.Processing.Logging;

internal static class AppLogCaptureContext
{
    private static readonly AsyncLocal<int> SuppressDepth = new();

    public static bool IsSuppressed => SuppressDepth.Value > 0;

    public static IDisposable BeginSuppressed()
    {
        SuppressDepth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SuppressDepth.Value = Math.Max(0, SuppressDepth.Value - 1);
        }
    }
}
