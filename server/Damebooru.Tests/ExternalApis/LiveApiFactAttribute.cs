namespace Damebooru.Tests;

/// <summary>
/// For tests that call real external APIs. They only run when DAMEBOORU_LIVE_API_TESTS=1.
/// </summary>
public sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DAMEBOORU_LIVE_API_TESTS") != "1")
        {
            Skip = "Calls a live API. Set DAMEBOORU_LIVE_API_TESTS=1 to run.";
        }
    }
}
