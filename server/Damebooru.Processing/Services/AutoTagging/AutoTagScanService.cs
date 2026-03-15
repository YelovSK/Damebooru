using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class AutoTagScanService
{
    private sealed record PostScanTarget(int Id, int LibraryId, string RelativePath, string ContentHash, string ContentType);

    private readonly DamebooruDbContext _db;
    private readonly ISauceNaoClient _sauceNaoClient;
    private readonly IReadOnlyDictionary<AutoTagProvider, IExternalPostMetadataClient> _metadataClients;
    private readonly decimal _minimumSauceNaoSimilarity;

    public AutoTagScanService(
        DamebooruDbContext db,
        ISauceNaoClient sauceNaoClient,
        IEnumerable<IExternalPostMetadataClient> metadataClients,
        DamebooruConfig config)
    {
        _db = db;
        _sauceNaoClient = sauceNaoClient;
        _metadataClients = metadataClients.ToDictionary(client => client.Provider);
        _minimumSauceNaoSimilarity = Math.Max(0m, config.ExternalApis.SauceNao.MinimumSimilarity);
    }

    public async Task<AutoTagScanResult> ScanPostAsync(int postId, CancellationToken cancellationToken = default)
    {
        var post = await _db.Posts
            .AsNoTracking()
            .Where(p => p.Id == postId)
            .Select(p => new PostScanTarget(p.Id, p.LibraryId, p.RelativePath, p.ContentHash, p.ContentType))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Post {postId} was not found.");

        if (!CanAutoTag(post))
        {
            throw new InvalidOperationException($"Post {postId} is not eligible for auto-tagging. Only image posts are supported.");
        }

        var scan = await _db.PostAutoTagScans
            .Include(s => s.Steps)
            .Include(s => s.Candidates)
            .Include(s => s.Sources)
            .Include(s => s.Tags)
            .FirstOrDefaultAsync(s => s.PostId == postId, cancellationToken);

        var isStaleContentReset = false;
        if (scan == null)
        {
            scan = CreateScan(postId, post.ContentHash, _minimumSauceNaoSimilarity);
            _db.PostAutoTagScans.Add(scan);
            isStaleContentReset = true;
        }
        else if (!string.Equals(scan.ContentHash, post.ContentHash, StringComparison.Ordinal)
            || scan.SauceNaoMinimumSimilarity != _minimumSauceNaoSimilarity)
        {
            ResetScan(scan, post.ContentHash, _minimumSauceNaoSimilarity);
            isStaleContentReset = true;
        }

        scan.Status = AutoTagScanStatus.InProgress;
        scan.LastStartedAtUtc = DateTime.UtcNow;
        scan.LastError = null;

        EnsureStep(scan, AutoTagProvider.SauceNao);
        EnsureStep(scan, AutoTagProvider.Danbooru);
        EnsureStep(scan, AutoTagProvider.Gelbooru);
        AutoTagExecutionDirective? directive = null;

        await _db.SaveChangesAsync(cancellationToken);

        directive = await RunSauceNaoStepAsync(scan, post, cancellationToken);

        if (directive == null)
        {
            foreach (var provider in new[] { AutoTagProvider.Danbooru, AutoTagProvider.Gelbooru })
            {
                await RunMetadataStepAsync(scan, provider, cancellationToken);
            }
        }

        scan.Status = CalculateOverallStatus(scan);
        scan.LastCompletedAtUtc = DateTime.UtcNow;
        scan.LastError = scan.Status == AutoTagScanStatus.Completed
            ? null
            : string.Join(" | ", scan.Steps.Where(step => !string.IsNullOrWhiteSpace(step.LastError)).Select(step => $"{step.Provider}: {step.LastError}"));

        await _db.SaveChangesAsync(cancellationToken);
        return new AutoTagScanResult(postId, scan.Status, isStaleContentReset, ShouldApply(scan, directive), directive);
    }

    private async Task<AutoTagExecutionDirective?> RunSauceNaoStepAsync(PostAutoTagScan scan, PostScanTarget post, CancellationToken cancellationToken)
    {
        var step = EnsureStep(scan, AutoTagProvider.SauceNao);
        if (!ShouldRunStep(step))
        {
            return null;
        }

        PrepareStepAttempt(step);

        try
        {
            var libraryRoot = await _db.Libraries
                .AsNoTracking()
                .Where(l => l.Id == post.LibraryId)
                .Select(l => l.Path)
                .FirstAsync(cancellationToken);
            var filePath = Path.Combine(libraryRoot, post.RelativePath);

            await using var stream = File.OpenRead(filePath);
            var result = await _sauceNaoClient.SearchAsync(stream, Path.GetFileName(filePath), cancellationToken: cancellationToken);
            var acceptedMatches = GetAcceptedSauceNaoMatches(result).ToList();

            ReplaceProviderCandidates(scan, BuildCandidates(acceptedMatches));
            ReplaceProviderSources(scan, AutoTagProvider.SauceNao, acceptedMatches.SelectMany(match => match.ExternalUrls));

            step.Status = AutoTagScanStepStatus.Succeeded;
            step.NextRetryAtUtc = null;
            step.LastError = null;
            return null;
        }
        catch (Exception ex)
        {
            return HandleStepFailure(step, ex);
        }
    }

    private async Task RunMetadataStepAsync(PostAutoTagScan scan, AutoTagProvider provider, CancellationToken cancellationToken)
    {
        var step = EnsureStep(scan, provider);
        var candidate = scan.Candidates
            .Where(c => c.Provider == provider)
            .OrderByDescending(c => c.Similarity)
            .FirstOrDefault();

        if (candidate == null)
        {
            step.Status = AutoTagScanStepStatus.Skipped;
            step.ExternalPostId = null;
            step.NextRetryAtUtc = null;
            step.LastError = null;
            RemoveProviderMetadata(scan, provider);
            return;
        }

        var candidateChanged = step.ExternalPostId != candidate.ExternalPostId;
        if (!candidateChanged && step.Status == AutoTagScanStepStatus.Succeeded)
        {
            return;
        }

        if (!candidateChanged && !ShouldRunStep(step))
        {
            return;
        }

        if (!_metadataClients.TryGetValue(provider, out var client))
        {
            throw new InvalidOperationException($"No metadata client is registered for provider {provider}.");
        }

        if (candidateChanged)
        {
            RemoveProviderMetadata(scan, provider);
        }

        PrepareStepAttempt(step);
        step.ExternalPostId = candidate.ExternalPostId;

        try
        {
            var details = await client.GetPostDetailsAsync(candidate.ExternalPostId, cancellationToken);
            if (details == null)
            {
                step.Status = AutoTagScanStepStatus.Skipped;
                step.NextRetryAtUtc = null;
                step.LastError = null;
                RemoveProviderMetadata(scan, provider);
                return;
            }

            ReplaceProviderSources(scan, provider, details.SourceUrls.Append(details.CanonicalUrl));
            ReplaceProviderTags(scan, provider, details.PostId, details.Tags);

            step.Status = AutoTagScanStepStatus.Succeeded;
            step.NextRetryAtUtc = null;
            step.LastError = null;
            step.ExternalPostId = details.PostId;
        }
        catch (Exception ex)
        {
            HandleStepFailure(step, ex);
        }
    }

    private static PostAutoTagScan CreateScan(int postId, string contentHash, decimal minimumSauceNaoSimilarity)
        => new()
        {
            PostId = postId,
            ContentHash = contentHash,
            SauceNaoMinimumSimilarity = minimumSauceNaoSimilarity,
            Status = AutoTagScanStatus.Pending
        };

    private static void ResetScan(PostAutoTagScan scan, string contentHash, decimal minimumSauceNaoSimilarity)
    {
        scan.ContentHash = contentHash;
        scan.SauceNaoMinimumSimilarity = minimumSauceNaoSimilarity;
        scan.Status = AutoTagScanStatus.Pending;
        scan.LastCompletedAtUtc = null;
        scan.LastError = null;
        scan.Candidates.Clear();
        scan.Sources.Clear();
        scan.Tags.Clear();
        foreach (var step in scan.Steps)
        {
            step.Status = AutoTagScanStepStatus.Pending;
            step.AttemptCount = 0;
            step.LastAttemptAtUtc = null;
            step.NextRetryAtUtc = null;
            step.LastError = null;
            step.ExternalPostId = null;
        }
    }

    private static PostAutoTagScanStep EnsureStep(PostAutoTagScan scan, AutoTagProvider provider)
    {
        var step = scan.Steps.FirstOrDefault(s => s.Provider == provider);
        if (step != null)
        {
            return step;
        }

        step = new PostAutoTagScanStep { Provider = provider };
        scan.Steps.Add(step);
        return step;
    }

    private static bool ShouldRunStep(PostAutoTagScanStep step)
        => (step.Status is AutoTagScanStepStatus.Pending or AutoTagScanStepStatus.PermanentFailure or AutoTagScanStepStatus.RetryableFailure)
           && (step.NextRetryAtUtc == null || step.NextRetryAtUtc <= DateTime.UtcNow);

    private static void PrepareStepAttempt(PostAutoTagScanStep step)
    {
        step.AttemptCount++;
        step.LastAttemptAtUtc = DateTime.UtcNow;
        step.Status = AutoTagScanStepStatus.Running;
        step.LastError = null;
    }

    private static AutoTagExecutionDirective? HandleStepFailure(PostAutoTagScanStep step, Exception exception)
    {
        var providerException = exception as ExternalProviderException;
        step.Status = providerException?.IsRetryable == true
            ? AutoTagScanStepStatus.RetryableFailure
            : AutoTagScanStepStatus.PermanentFailure;
        step.LastError = exception.Message;
        step.NextRetryAtUtc = providerException?.IsRetryable == true
            ? DateTime.UtcNow.Add(providerException.RetryAfter ?? TimeSpan.FromMinutes(Math.Min(60, Math.Max(1, step.AttemptCount * 5))))
            : null;

        return providerException == null || (!providerException.StopCurrentRun && providerException.RetryAfter == null)
            ? null
            : new AutoTagExecutionDirective(providerException.Provider, providerException.Message, providerException.RetryAfter, providerException.StopCurrentRun);
    }

    private IEnumerable<SauceNaoMatch> GetAcceptedSauceNaoMatches(SauceNaoSearchResult result)
        => result.Matches.Where(match => match.Similarity >= _minimumSauceNaoSimilarity);

    private static bool CanAutoTag(PostScanTarget post)
        => post.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
           || SupportedMedia.IsImage(Path.GetExtension(post.RelativePath));

    private static List<PostAutoTagScanCandidate> BuildCandidates(IEnumerable<SauceNaoMatch> matches)
    {
        var candidates = new Dictionary<(AutoTagProvider Provider, long ExternalPostId), PostAutoTagScanCandidate>();
        foreach (var match in matches)
        {
            AddCandidate(candidates, AutoTagProvider.Danbooru, match.DanbooruPostId, match.Similarity, $"https://danbooru.donmai.us/posts/{match.DanbooruPostId}");
            AddCandidate(candidates, AutoTagProvider.Gelbooru, match.GelbooruPostId, match.Similarity, $"https://gelbooru.com/index.php?page=post&s=view&id={match.GelbooruPostId}");
        }

        return candidates.Values.ToList();
    }

    private static void AddCandidate(Dictionary<(AutoTagProvider Provider, long ExternalPostId), PostAutoTagScanCandidate> candidates, AutoTagProvider provider, long? externalPostId, decimal similarity, string canonicalUrl)
    {
        if (!externalPostId.HasValue)
        {
            return;
        }

        var key = (provider, externalPostId.Value);
        if (candidates.TryGetValue(key, out var existing))
        {
            if (similarity > existing.Similarity)
            {
                existing.Similarity = similarity;
                existing.CanonicalUrl = canonicalUrl;
            }

            return;
        }

        candidates[key] = new PostAutoTagScanCandidate
        {
            Provider = provider,
            ExternalPostId = externalPostId.Value,
            Similarity = similarity,
            CanonicalUrl = canonicalUrl
        };
    }

    private static void ReplaceProviderCandidates(PostAutoTagScan scan, IReadOnlyCollection<PostAutoTagScanCandidate> candidates)
    {
        var existing = scan.Candidates.ToList();
        foreach (var candidate in existing)
        {
            scan.Candidates.Remove(candidate);
        }

        foreach (var candidate in candidates)
        {
            scan.Candidates.Add(candidate);
        }
    }

    private static void ReplaceProviderSources(PostAutoTagScan scan, AutoTagProvider provider, IEnumerable<string> urls)
    {
        foreach (var existing in scan.Sources.Where(source => source.Provider == provider).ToList())
        {
            scan.Sources.Remove(existing);
        }

        foreach (var url in urls.Select(url => url.Trim()).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            scan.Sources.Add(new PostAutoTagScanSource
            {
                Provider = provider,
                Url = url
            });
        }
    }

    private static void ReplaceProviderTags(PostAutoTagScan scan, AutoTagProvider provider, long externalPostId, IEnumerable<ExternalTagData> tags)
    {
        foreach (var existing in scan.Tags.Where(tag => tag.Provider == provider).ToList())
        {
            scan.Tags.Remove(existing);
        }

        foreach (var group in tags
                     .Select(tag => new ExternalTagData(TagService.SanitizeTagName(tag.Name), tag.Category))
                     .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
                     .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase))
        {
            scan.Tags.Add(new PostAutoTagScanTag
            {
                Provider = provider,
                ExternalPostId = externalPostId,
                Name = group.Key,
                Category = PickPreferredCategory(group.Select(tag => tag.Category))
            });
        }
    }

    private static void RemoveProviderMetadata(PostAutoTagScan scan, AutoTagProvider provider)
    {
        foreach (var existingSource in scan.Sources.Where(source => source.Provider == provider).ToList())
        {
            scan.Sources.Remove(existingSource);
        }

        foreach (var existingTag in scan.Tags.Where(tag => tag.Provider == provider).ToList())
        {
            scan.Tags.Remove(existingTag);
        }
    }

    private static TagCategoryKind PickPreferredCategory(IEnumerable<TagCategoryKind> categories)
    {
        foreach (var category in categories)
        {
            if (category != TagCategoryKind.General)
            {
                return category;
            }
        }

        return TagCategoryKind.General;
    }

    private static AutoTagScanStatus CalculateOverallStatus(PostAutoTagScan scan)
    {
        var hasRetryableFailure = scan.Steps.Any(step => step.Status == AutoTagScanStepStatus.RetryableFailure);
        var hasPermanentFailure = scan.Steps.Any(step => step.Status == AutoTagScanStepStatus.PermanentFailure);
        var hasSuccessOrSkip = scan.Steps.Any(step => step.Status is AutoTagScanStepStatus.Succeeded or AutoTagScanStepStatus.Skipped);

        if (!hasRetryableFailure && !hasPermanentFailure && scan.Steps.All(step => step.Status is AutoTagScanStepStatus.Succeeded or AutoTagScanStepStatus.Skipped))
        {
            return AutoTagScanStatus.Completed;
        }

        if (hasSuccessOrSkip)
        {
            return AutoTagScanStatus.Partial;
        }

        return AutoTagScanStatus.Failed;
    }

    private static bool ShouldApply(PostAutoTagScan scan, AutoTagExecutionDirective? directive)
        => directive == null && scan.Steps.Any(step => step.Status is AutoTagScanStepStatus.Succeeded or AutoTagScanStepStatus.Skipped);
}
