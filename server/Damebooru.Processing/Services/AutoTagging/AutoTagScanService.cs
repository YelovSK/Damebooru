using Damebooru.Core;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class AutoTagScanService
{
    private sealed record PostScanTarget(int Id, int LibraryId, string RelativePath, string ContentHash, string ContentType);

    private readonly DamebooruDbContext _db;
    private readonly IReadOnlyDictionary<AutoTagProvider, IExternalPostDiscoveryClient> _discoveryClients;
    private readonly IReadOnlyDictionary<AutoTagProvider, IExternalPostMetadataClient> _metadataClients;
    private readonly decimal _minimumSauceNaoSimilarity;
    private readonly ILogger<AutoTagScanService> _logger;

    public AutoTagScanService(
        DamebooruDbContext db,
        IEnumerable<IExternalPostDiscoveryClient> discoveryClients,
        IEnumerable<IExternalPostMetadataClient> metadataClients,
        DamebooruConfig config,
        ILogger<AutoTagScanService> logger)
    {
        _db = db;
        _discoveryClients = discoveryClients.ToDictionary(client => client.Provider);
        _metadataClients = metadataClients.ToDictionary(client => client.Provider);
        _minimumSauceNaoSimilarity = Math.Max(0m, config.ExternalApis.SauceNao.MinimumSimilarity);
        _logger = logger;
    }

    public async Task<AutoTagScanResult> ScanPostAsync(int postId, CancellationToken cancellationToken = default)
        => await ScanPostAsync(postId, forceRefresh: false, cancellationToken);

    public async Task<AutoTagScanResult> ScanPostAsync(int postId, bool forceRefresh, CancellationToken cancellationToken = default)
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
        else if (forceRefresh)
        {
            ResetScan(scan, post.ContentHash, _minimumSauceNaoSimilarity, preserveMd5Hash: true);
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

        foreach (var provider in AutoTagDiscoveryPlan.OrderedDiscoveryProviders)
        {
            EnsureStep(scan, provider, AutoTagScanStepKind.Discovery);
        }

        foreach (var provider in AutoTagDiscoveryPlan.MetadataProviders)
        {
            EnsureStep(scan, provider, AutoTagScanStepKind.Metadata);
        }

        AutoTagExecutionDirective? directive = null;

        await _db.SaveChangesAsync(cancellationToken);

        var libraryRoot = await _db.Libraries
            .AsNoTracking()
            .Where(l => l.Id == post.LibraryId)
            .Select(l => l.Path)
            .FirstAsync(cancellationToken);
        var filePath = Path.Combine(libraryRoot, post.RelativePath);
        var md5Hash = await EnsureMd5HashAsync(scan, filePath, cancellationToken);
        var discoveryContext = new PostDiscoveryContext(post.Id, post.RelativePath, post.ContentType, filePath, post.ContentHash, md5Hash);

        directive = await RunDiscoveryPipelineAsync(scan, discoveryContext, cancellationToken);

        if (directive == null)
        {
            foreach (var provider in AutoTagDiscoveryPlan.MetadataProviders)
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

    private async Task<AutoTagExecutionDirective?> RunDiscoveryPipelineAsync(PostAutoTagScan scan, PostDiscoveryContext context, CancellationToken cancellationToken)
    {
        foreach (var provider in AutoTagDiscoveryPlan.OrderedDiscoveryProviders)
        {
            if (scan.Candidates.Count > 0)
            {
                MarkRemainingDiscoveryStepsSkipped(scan, provider);
                return null;
            }

            var step = EnsureStep(scan, provider, AutoTagScanStepKind.Discovery);
            if (!ShouldRunStep(step))
            {
                continue;
            }

            if (!_discoveryClients.TryGetValue(provider, out var client))
            {
                throw new InvalidOperationException($"No discovery client is registered for provider {provider}.");
            }

            PrepareStepAttempt(step);

            try
            {
                var result = await client.DiscoverAsync(context, cancellationToken);
                ReplaceProviderDiscoveryArtifacts(scan, provider, result);

                step.Status = AutoTagScanStepStatus.Succeeded;
                step.NextRetryAtUtc = null;
                step.LastError = null;

                if (scan.Candidates.Count > 0)
                {
                    MarkRemainingDiscoveryStepsSkipped(scan, provider);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-tag discovery failed for post {PostId} ({RelativePath}) at provider {Provider}", context.PostId, context.RelativePath, provider);
                return HandleStepFailure(step, ex);
            }
        }

        return null;
    }

    private async Task RunMetadataStepAsync(PostAutoTagScan scan, AutoTagProvider provider, CancellationToken cancellationToken)
    {
        var step = EnsureStep(scan, provider, AutoTagScanStepKind.Metadata);
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
            _logger.LogWarning(ex, "Auto-tag metadata scan failed for post {PostId} at provider {Provider}", scan.PostId, provider);
            HandleStepFailure(step, ex);
        }
    }

    private static PostAutoTagScan CreateScan(int postId, string contentHash, decimal minimumSauceNaoSimilarity)
        => new()
        {
            PostId = postId,
            ContentHash = contentHash,
            DiscoveryVersion = AutoTagDiscoveryPlan.Version,
            SauceNaoMinimumSimilarity = minimumSauceNaoSimilarity,
            Status = AutoTagScanStatus.Pending
        };

    private static void ResetScan(PostAutoTagScan scan, string contentHash, decimal minimumSauceNaoSimilarity, bool preserveMd5Hash = false)
    {
        scan.ContentHash = contentHash;
        scan.DiscoveryVersion = AutoTagDiscoveryPlan.Version;
        if (!preserveMd5Hash)
        {
            scan.Md5Hash = null;
        }
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

    private static PostAutoTagScanStep EnsureStep(PostAutoTagScan scan, AutoTagProvider provider, AutoTagScanStepKind kind)
    {
        var step = scan.Steps.FirstOrDefault(s => s.Provider == provider && s.Kind == kind);
        if (step != null)
        {
            return step;
        }

        step = new PostAutoTagScanStep { Provider = provider, Kind = kind };
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

    private static bool CanAutoTag(PostScanTarget post)
        => post.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
           || SupportedMedia.IsImage(Path.GetExtension(post.RelativePath));

    private void ReplaceProviderDiscoveryArtifacts(PostAutoTagScan scan, AutoTagProvider discoveryProvider, ExternalDiscoveryResult result)
    {
        ReplaceProviderSources(scan, discoveryProvider, result.Matches.Select(match => match.Url));

        foreach (var existingCandidate in scan.Candidates.Where(candidate => candidate.DiscoveryProvider == discoveryProvider).ToList())
        {
            scan.Candidates.Remove(existingCandidate);
        }

        var metadataReferences = result.Matches
            .SelectMany(match => _metadataClients.Values
                .Select(client => client.TryParseReference(match.Url, match.Score))
                .Where(reference => reference != null)
                .Select(reference => reference!))
            .GroupBy(reference => (reference.Provider, reference.ExternalPostId), reference => reference)
            .Select(group => group.OrderByDescending(reference => reference.Score).First())
            .ToList();

        foreach (var candidate in metadataReferences)
        {
            scan.Candidates.Add(new PostAutoTagScanCandidate
            {
                DiscoveryProvider = discoveryProvider,
                Provider = candidate.Provider,
                ExternalPostId = candidate.ExternalPostId,
                Similarity = candidate.Score,
                CanonicalUrl = candidate.CanonicalUrl
            });
        }
    }

    private static void MarkRemainingDiscoveryStepsSkipped(PostAutoTagScan scan, AutoTagProvider lastAttemptedProvider)
    {
        var seenProvider = false;
        foreach (var provider in AutoTagDiscoveryPlan.OrderedDiscoveryProviders)
        {
            if (provider == lastAttemptedProvider)
            {
                seenProvider = true;
                continue;
            }

            if (!seenProvider)
            {
                continue;
            }

            var step = EnsureStep(scan, provider, AutoTagScanStepKind.Discovery);
            if (step.Status == AutoTagScanStepStatus.Pending)
            {
                step.Status = AutoTagScanStepStatus.Skipped;
                step.NextRetryAtUtc = null;
                step.LastError = null;
            }
        }
    }

    private async Task<string> EnsureMd5HashAsync(PostAutoTagScan scan, string filePath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(scan.Md5Hash))
        {
            return scan.Md5Hash;
        }

        await using var stream = File.OpenRead(filePath);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, cancellationToken);
        scan.Md5Hash = Convert.ToHexStringLower(hash);
        await _db.SaveChangesAsync(cancellationToken);
        return scan.Md5Hash;
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
