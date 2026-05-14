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

        var result = new MutableApplyResult();
        await ApplyTagsAsync(post, previewResult.Value.Tags, settings.ApplyThreshold, result, cancellationToken);
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
            .Select(p => p.PostFiles
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

    private async Task ApplyTagsAsync(
        Post post,
        IReadOnlyCollection<AiTagSuggestionDto> suggestions,
        decimal applyThreshold,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        var desiredTags = suggestions
            .Where(tag => tag.Score >= applyThreshold)
            .Select(tag => new
            {
                Name = TagService.SanitizeTagName(tag.Name),
                tag.Category
            })
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Category = group.Select(tag => tag.Category).FirstOrDefault(category => category != TagCategoryKind.General)
            })
            .ToList();

        var desiredNames = desiredTags
            .Select(tag => tag.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingAiLinks = post.PostTags
            .Where(link => link.Source == PostTagSource.Ai)
            .ToList();

        foreach (var link in existingAiLinks.Where(link => !desiredNames.Contains(link.Tag.Name)).ToList())
        {
            _db.PostTags.Remove(link);
            post.PostTags.Remove(link);
            result.RemovedTags++;
        }

        var requiredNames = desiredTags.Select(tag => tag.Name).ToList();
        var tagsByName = _db.Tags.Local
            .Where(tag => requiredNames.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);

        var persistedTags = await _db.Tags
            .Where(tag => requiredNames.Contains(tag.Name))
            .ToDictionaryAsync(tag => tag.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var persistedTag in persistedTags)
        {
            tagsByName[persistedTag.Key] = persistedTag.Value;
        }

        foreach (var desiredTag in desiredTags)
        {
            if (!tagsByName.TryGetValue(desiredTag.Name, out var tag))
            {
                tag = new Tag
                {
                    Name = desiredTag.Name,
                    Category = desiredTag.Category
                };
                _db.Tags.Add(tag);
                tagsByName[tag.Name] = tag;
            }
            else if (ShouldUpgradeCategory(tag.Category, desiredTag.Category))
            {
                tag.Category = desiredTag.Category;
                result.UpdatedTagCategories++;
            }

            var alreadyLinked = post.PostTags.Any(link =>
                link.Source == PostTagSource.Ai
                && string.Equals(link.Tag.Name, desiredTag.Name, StringComparison.OrdinalIgnoreCase));
            if (!alreadyLinked)
            {
                var link = new PostTag
                {
                    PostId = post.Id,
                    Tag = tag,
                    Source = PostTagSource.Ai
                };
                _db.PostTags.Add(link);
                post.PostTags.Add(link);
                result.AddedTags++;
            }
        }
    }

    private static bool ShouldUpgradeCategory(TagCategoryKind current, TagCategoryKind discovered)
        => current == TagCategoryKind.General && discovered != TagCategoryKind.General;

    private sealed class MutableApplyResult
    {
        public int AddedTags { get; set; }
        public int RemovedTags { get; set; }
        public int UpdatedTagCategories { get; set; }
    }
}
