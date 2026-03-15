using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class PostAutoTaggingService
{
    private readonly AutoTagScanService _scanService;
    private readonly AutoTagApplyService _applyService;
    private readonly PostReadService _postReadService;
    private readonly AutoTagConfigurationValidator _configurationValidator;

    public PostAutoTaggingService(
        AutoTagScanService scanService,
        AutoTagApplyService applyService,
        PostReadService postReadService,
        AutoTagConfigurationValidator configurationValidator)
    {
        _scanService = scanService;
        _applyService = applyService;
        _postReadService = postReadService;
        _configurationValidator = configurationValidator;
    }

    public async Task<Result<AutoTagPostResultDto>> AutoTagAsync(int postId, CancellationToken cancellationToken = default)
    {
        try
        {
            _configurationValidator.EnsureConfigured();
            var scanResult = await _scanService.ScanPostAsync(postId, cancellationToken);
            var applyResult = await _applyService.ApplyScanAsync(postId, cancellationToken);
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
}
