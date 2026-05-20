using Damebooru.Core.Config;
using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AiTagging;

public sealed class AiTaggingService
{
    private sealed record AiTaggingCandidate(
        string LibraryPath,
        string RelativePath,
        string ContentType);

    private readonly DamebooruDbContext _db;
    private readonly DamebooruConfig _config;
    private readonly IAiTaggingClient _client;
    private readonly PostReadService _postReadService;
    private readonly AiTaggingSettingsService _settingsService;

    public AiTaggingService(
        DamebooruDbContext db,
        DamebooruConfig config,
        IAiTaggingClient client,
        PostReadService postReadService,
        AiTaggingSettingsService settingsService)
    {
        _db = db;
        _config = config;
        _client = client;
        _postReadService = postReadService;
        _settingsService = settingsService;
    }

    public async Task<Result<AiTagPreviewDto>> PreviewAsync(int postId, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetOrCreateEntityAsync(cancellationToken);
        return await GeneratePreviewAsync(postId, settings.SuggestionThreshold, settings.ApplyThreshold, cancellationToken);
    }

    public async Task<Result<AiTagPostResultDto>> ApplyAsync(int postId, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetOrCreateEntityAsync(cancellationToken);
        var previewResult = await GeneratePreviewAsync(postId, settings.SuggestionThreshold, settings.ApplyThreshold, cancellationToken);
        if (!previewResult.IsSuccess || previewResult.Value == null)
        {
            return Result<AiTagPostResultDto>.Failure(previewResult.Error ?? OperationError.ServiceUnavailable, previewResult.Message ?? "AI tagging failed.");
        }

        var post = await _db.Posts
            .Include(p => p.PostTags)
                .ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);

        if (post == null)
        {
            return Result<AiTagPostResultDto>.Failure(OperationError.NotFound, "Post not found.");
        }

        var result = await PostTagReconciler.ReconcileAsync(
            _db,
            post,
            PostTagSource.Ai,
            previewResult.Value.Tags
                .Where(tag => tag.Score >= settings.ApplyThreshold)
                .Select(tag => new PostTagReconciliationTarget(tag.Name, tag.Category)),
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var postResult = await _postReadService.GetPostAsync(postId, cancellationToken);
        if (!postResult.IsSuccess || postResult.Value == null)
        {
            return Result<AiTagPostResultDto>.Failure(OperationError.NotFound, "Post not found after AI tagging.");
        }

        return Result<AiTagPostResultDto>.Success(new AiTagPostResultDto
        {
            AddedTags = result.AddedTags,
            RemovedTags = result.RemovedTags,
            UpdatedTagCategories = result.UpdatedTagCategories,
            Preview = previewResult.Value,
            Post = postResult.Value
        });
    }

    private async Task<Result<AiTagPreviewDto>> GeneratePreviewAsync(
        int postId,
        decimal suggestionThreshold,
        decimal applyThreshold,
        CancellationToken cancellationToken)
    {
        if (!_config.AiTagging.Enabled)
        {
            return Result<AiTagPreviewDto>.Failure(OperationError.InvalidInput, "AI tagging is not enabled.");
        }

        var candidate = await _db.Posts
            .AsNoTracking()
            .Where(p => p.Id == postId)
            .Select(p => p.PrimaryPostFile != null && EF.Functions.Like(p.PrimaryPostFile.ContentType, "image/%")
                ? new AiTaggingCandidate(
                    p.PrimaryPostFile.Library.Path,
                    p.PrimaryPostFile.RelativePath,
                    p.PrimaryPostFile.ContentType)
                : p.PostFiles
                .Where(pf => EF.Functions.Like(pf.ContentType, "image/%"))
                .OrderBy(pf => pf.Id)
                .Select(pf => new AiTaggingCandidate(
                    pf.Library.Path,
                    pf.RelativePath,
                    pf.ContentType))
                .FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate == null)
        {
            var postExists = await _db.Posts
                .AsNoTracking()
                .AnyAsync(p => p.Id == postId, cancellationToken);

            return postExists
                ? Result<AiTagPreviewDto>.Failure(OperationError.InvalidInput, "Post has no image file for AI tagging.")
                : Result<AiTagPreviewDto>.Failure(OperationError.NotFound, "Post not found.");
        }

        if (!SafeSubpathResolver.TryResolve(candidate.LibraryPath, candidate.RelativePath, out var fullPath))
        {
            return Result<AiTagPreviewDto>.Failure(OperationError.InvalidInput, "Invalid file path.");
        }

        if (!File.Exists(fullPath))
        {
            return Result<AiTagPreviewDto>.Failure(OperationError.NotFound, "File not found on disk.");
        }

        try
        {
            var isReady = await _client.IsReadyAsync(cancellationToken);
            if (!isReady)
            {
                return Result<AiTagPreviewDto>.Failure(OperationError.ServiceUnavailable, "AI tagging service is not ready.");
            }

            await using var fileStream = File.OpenRead(fullPath);
            var result = await _client.TagAsync(
                fileStream,
                Path.GetFileName(fullPath),
                candidate.ContentType,
                threshold: suggestionThreshold,
                cancellationToken: cancellationToken);

            return Result<AiTagPreviewDto>.Success(new AiTagPreviewDto
            {
                Enabled = true,
                Ready = true,
                Model = result.Model,
                Provider = result.Provider,
                Threshold = result.Threshold,
                ApplyThreshold = applyThreshold,
                MinConfidence = result.MinConfidence,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                Tags = result.Tags
                    .Select(tag => new AiTagSuggestionDto
                    {
                        Name = tag.Name,
                        Score = tag.Score,
                        Category = tag.Category,
                        RawCategory = tag.RawCategory,
                        MeetsApplyThreshold = tag.Score >= applyThreshold
                    })
                    .ToList()
            });
        }
        catch (HttpRequestException ex)
        {
            return Result<AiTagPreviewDto>.Failure(OperationError.ServiceUnavailable, $"AI tagging service is unavailable: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<AiTagPreviewDto>.Failure(OperationError.ServiceUnavailable, $"AI tagging service timed out: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Result<AiTagPreviewDto>.Failure(OperationError.ServiceUnavailable, ex.Message);
        }
    }

}
