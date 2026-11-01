using Damebooru.Core.Interfaces;

namespace Damebooru.Processing.Services;

internal sealed class JobReporter(JobInfo job) : IJobReporter
{
    public void Report(string activity, int? current = null, int? total = null)
        => job.State = new JobState { ActivityText = activity, ProgressCurrent = current, ProgressTotal = total };
}
