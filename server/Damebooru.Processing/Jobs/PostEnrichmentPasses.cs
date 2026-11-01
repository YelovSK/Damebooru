using Damebooru.Core.Interfaces;

namespace Damebooru.Processing.Jobs;

/// <summary>
/// Fills in whatever metadata, similarity hashes and thumbnails posts are missing, one pass per kind,
/// so a fresh library becomes browsable before its slow passes finish.
/// </summary>
public sealed class PostEnrichmentPasses(
    ExtractMetadataJob metadata,
    ComputeSimilarityJob similarity,
    GenerateThumbnailsJob thumbnails)
{
    public async Task<string> RunMissingAsync(JobContext context)
    {
        var passContext = new JobContext
        {
            JobId = context.JobId,
            CancellationToken = context.CancellationToken,
            Reporter = context.Reporter,
            Mode = JobMode.Missing,
        };

        IJob[] passes = [metadata, similarity, thumbnails];
        var summaries = new List<string>();
        foreach (var pass in passes)
        {
            summaries.Add(await pass.ExecuteAsync(passContext));
        }

        return string.Join(" ", summaries);
    }
}
