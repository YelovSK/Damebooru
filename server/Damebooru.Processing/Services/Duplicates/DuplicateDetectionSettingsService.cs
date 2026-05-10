using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Duplicates;

public sealed class DuplicateDetectionSettingsService
{
    private const int SettingsId = 1;
    private const int MinimumThresholdPercent = 50;
    private const int MaximumThresholdPercent = 100;

    private readonly DamebooruDbContext _db;

    public DuplicateDetectionSettingsService(DamebooruDbContext db)
    {
        _db = db;
    }

    public async Task<DuplicateDetectionSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateEntityAsync(cancellationToken);
        return ToDto(settings);
    }

    public async Task<Result<DuplicateDetectionSettingsDto>> UpdateAsync(
        DuplicateDetectionSettingsDto dto,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(dto);
        if (validationError != null)
        {
            return Result<DuplicateDetectionSettingsDto>.Failure(OperationError.InvalidInput, validationError);
        }

        var settings = await GetOrCreateEntityAsync(cancellationToken);
        settings.PerceptualSimilarityThresholdPercent = dto.PerceptualSimilarityThresholdPercent;

        await _db.SaveChangesAsync(cancellationToken);
        return Result<DuplicateDetectionSettingsDto>.Success(ToDto(settings));
    }

    private async Task<DuplicateDetectionSettings> GetOrCreateEntityAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.DuplicateDetectionSettings.FirstOrDefaultAsync(s => s.Id == SettingsId, cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new DuplicateDetectionSettings { Id = SettingsId };
        _db.DuplicateDetectionSettings.Add(settings);
        await _db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static string? Validate(DuplicateDetectionSettingsDto dto)
    {
        if (!IsValidThreshold(dto.PerceptualSimilarityThresholdPercent))
        {
            return $"Perceptual similarity threshold must be between {MinimumThresholdPercent} and {MaximumThresholdPercent}.";
        }

        return null;
    }

    private static bool IsValidThreshold(int value)
        => value is >= MinimumThresholdPercent and <= MaximumThresholdPercent;

    private static DuplicateDetectionSettingsDto ToDto(DuplicateDetectionSettings settings)
        => new()
        {
            PerceptualSimilarityThresholdPercent = settings.PerceptualSimilarityThresholdPercent
        };
}
