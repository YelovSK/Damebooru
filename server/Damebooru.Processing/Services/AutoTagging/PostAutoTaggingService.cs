using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class PostAutoTaggingService
{
    private readonly AutoTagScanService _scanService;
    private readonly AutoTagApplyService _applyService;
    private readonly PostReadService _postReadService;
    private readonly AutoTagConfigurationValidator _configurationValidator;
    private readonly AutoTagDiscoverySettingsService _discoverySettingsService;
    private readonly DamebooruDbContext _db;

    public PostAutoTaggingService(
        AutoTagScanService scanService,
        AutoTagApplyService applyService,
        PostReadService postReadService,
        AutoTagConfigurationValidator configurationValidator,
        AutoTagDiscoverySettingsService discoverySettingsService,
        DamebooruDbContext db)
    {
        _scanService = scanService;
        _applyService = applyService;
        _postReadService = postReadService;
        _configurationValidator = configurationValidator;
        _discoverySettingsService = discoverySettingsService;
        _db = db;
    }

    public async Task<Result<PostAutoTagStatusDto>> GetStatusAsync(int postId, CancellationToken cancellationToken = default)
    {
        var postExists = await _db.Posts
            .AsNoTracking()
            .AnyAsync(p => p.Id == postId, cancellationToken);
        if (!postExists)
        {
            return Result<PostAutoTagStatusDto>.Failure(OperationError.NotFound, "Post not found.");
        }

        var enabledDiscoveryProviders = (await _discoverySettingsService.GetEnabledDiscoveryProvidersAsync(cancellationToken)).ToHashSet();
        var scan = await _db.PostAutoTagScans
            .AsNoTracking()
            .Include(s => s.Steps)
            .Include(s => s.Candidates)
            .Include(s => s.Sources)
            .Include(s => s.Tags)
            .FirstOrDefaultAsync(s => s.PostId == postId, cancellationToken);

        if (scan == null)
        {
            return Result<PostAutoTagStatusDto>.Success(new PostAutoTagStatusDto
            {
                HasScan = false,
                DiscoveryProviders = AutoTagDiscoveryPlan.OrderedDiscoveryProviders
                    .Select(provider => CreateMissingProviderStatus(provider, AutoTagScanStepKind.Discovery, enabledDiscoveryProviders.Contains(provider)))
                    .ToList(),
                MetadataProviders = AutoTagDiscoveryPlan.MetadataProviders
                    .Select(provider => CreateMissingProviderStatus(provider, AutoTagScanStepKind.Metadata, isEnabled: true))
                    .ToList()
            });
        }

        return Result<PostAutoTagStatusDto>.Success(new PostAutoTagStatusDto
        {
            HasScan = true,
            ScanStatus = scan.Status,
            LastStartedAtUtc = scan.LastStartedAtUtc,
            LastCompletedAtUtc = scan.LastCompletedAtUtc,
            DiscoveryProviders = AutoTagDiscoveryPlan.OrderedDiscoveryProviders
                .Select(provider => CreateProviderStatus(scan, provider, AutoTagScanStepKind.Discovery, enabledDiscoveryProviders.Contains(provider)))
                .ToList(),
            MetadataProviders = AutoTagDiscoveryPlan.MetadataProviders
                .Select(provider => CreateProviderStatus(scan, provider, AutoTagScanStepKind.Metadata, isEnabled: true))
                .ToList(),
            Candidates = scan.Candidates
                .OrderByDescending(candidate => candidate.Similarity)
                .Select(candidate => new PostAutoTagCandidateDto
                {
                    DiscoveryProvider = candidate.DiscoveryProvider,
                    Provider = candidate.Provider,
                    ExternalPostId = candidate.ExternalPostId,
                    Similarity = candidate.Similarity,
                    CanonicalUrl = candidate.CanonicalUrl
                })
                .ToList()
        });
    }

    public async Task<Result<AutoTagPostResultDto>> AutoTagAsync(int postId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _configurationValidator.EnsureConfiguredAsync(cancellationToken);
            var scanResult = await _scanService.ScanPostAsync(postId, forceRefresh: true, cancellationToken);
            var applyResult = scanResult.ShouldApply
                ? await _applyService.ApplyScanAsync(postId, cancellationToken)
                : AutoTagApplyResult.Empty;
            var postResult = await _postReadService.GetPostAsync(postId, cancellationToken);
            if (!postResult.IsSuccess || postResult.Value == null)
            {
                return Result<AutoTagPostResultDto>.Failure(OperationError.NotFound, "Post not found after auto-tagging.");
            }

            return Result<AutoTagPostResultDto>.Success(new AutoTagPostResultDto
            {
                ScanStatus = scanResult.Status,
                AddedTags = applyResult.AddedTags,
                RemovedTags = applyResult.RemovedTags,
                UpdatedTagCategories = applyResult.UpdatedTagCategories,
                AddedSources = applyResult.AddedSources,
                Post = postResult.Value
            });
        }
        catch (InvalidOperationException ex)
        {
            return Result<AutoTagPostResultDto>.Failure(OperationError.InvalidInput, ex.Message);
        }
    }

    private static PostAutoTagProviderStatusDto CreateMissingProviderStatus(AutoTagProvider provider, AutoTagScanStepKind kind, bool isEnabled)
        => new()
        {
            Provider = provider,
            Kind = kind,
            IsEnabled = isEnabled
        };

    private static PostAutoTagProviderStatusDto CreateProviderStatus(PostAutoTagScan scan, AutoTagProvider provider, AutoTagScanStepKind kind, bool isEnabled)
    {
        var step = scan.Steps.FirstOrDefault(s => s.Provider == provider && s.Kind == kind);
        return new PostAutoTagProviderStatusDto
        {
            Provider = provider,
            Kind = kind,
            Status = step?.Status,
            IsEnabled = isEnabled,
            AttemptCount = step?.AttemptCount ?? 0,
            LastAttemptAtUtc = step?.LastAttemptAtUtc,
            NextRetryAtUtc = step?.NextRetryAtUtc,
            LastError = step?.LastError,
            ExternalPostId = step?.ExternalPostId,
            TagCount = scan.Tags.Count(tag => tag.Provider == provider),
            SourceCount = scan.Sources.Count(source => source.Provider == provider)
        };
    }
}
