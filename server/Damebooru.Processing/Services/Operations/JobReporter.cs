using Damebooru.Core.Interfaces;

namespace Damebooru.Processing.Services;

internal sealed class JobReporter : IJobReporter
{
    private readonly Action<JobState> _onPublish;
    private readonly TimeSpan _minInterval;
    private readonly object _sync = new();

    private long _lastReportTicks;
    private JobState _state;

    public JobReporter(JobState initialState, TimeSpan minInterval, Action<JobState> onPublish)
    {
        _state = CloneState(initialState);
        _onPublish = onPublish;
        _minInterval = minInterval;
        _lastReportTicks = 0;

        _onPublish(CloneState(_state));
    }

    public JobState GetSnapshot()
    {
        lock (_sync)
        {
            return CloneState(_state);
        }
    }

    public void Update(JobState state)
    {
        if (state == null)
        {
            return;
        }

        JobState merged;
        lock (_sync)
        {
            _state = MergeState(_state, state);
            merged = CloneState(_state);
        }

        TryPublish(merged, force: false);
    }

    public void SetActivity(string? activityText)
        => Update(new JobState { ActivityText = activityText });

    public void SetProgress(int? current, int? total)
        => Update(new JobState { ProgressCurrent = current, ProgressTotal = total });

    public void ClearProgress()
        => Update(new JobState { ProgressCurrent = null, ProgressTotal = null });

    public void SetFinalText(string? finalText)
        => Update(new JobState { FinalText = finalText });

    public void SetResult(int? schemaVersion, string? resultJson)
        => Update(new JobState { ResultSchemaVersion = schemaVersion, ResultJson = resultJson });

    public void Flush()
        => TryPublish(GetSnapshot(), force: true);

    private void TryPublish(JobState state, bool force)
    {
        if (force)
        {
            Interlocked.Exchange(ref _lastReportTicks, DateTime.UtcNow.Ticks);
            _onPublish(state);
            return;
        }

        if (_minInterval > TimeSpan.Zero)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Interlocked.Read(ref _lastReportTicks);

            if (lastTicks != 0 && nowTicks - lastTicks < _minInterval.Ticks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastReportTicks, nowTicks, lastTicks) != lastTicks)
            {
                return;
            }
        }

        _onPublish(state);
    }

    private static JobState MergeState(JobState current, JobState update)
    {
        return new JobState
        {
            ActivityText = NormalizeText(update.ActivityText) ?? current.ActivityText,
            FinalText = NormalizeText(update.FinalText) ?? current.FinalText,
            ProgressCurrent = update.ProgressCurrent ?? current.ProgressCurrent,
            ProgressTotal = update.ProgressTotal ?? current.ProgressTotal,
            ResultSchemaVersion = update.ResultSchemaVersion ?? current.ResultSchemaVersion,
            ResultJson = NormalizeText(update.ResultJson) ?? current.ResultJson,
        };
    }

    private static JobState CloneState(JobState state)
    {
        return new JobState
        {
            ActivityText = state.ActivityText,
            FinalText = state.FinalText,
            ProgressCurrent = state.ProgressCurrent,
            ProgressTotal = state.ProgressTotal,
            ResultSchemaVersion = state.ResultSchemaVersion,
            ResultJson = state.ResultJson,
        };
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
