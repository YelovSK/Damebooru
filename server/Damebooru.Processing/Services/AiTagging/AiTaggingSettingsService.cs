using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AiTagging;

public sealed class AiTaggingSettingsService
{
    private const int SettingsId = 1;
    private const decimal MinimumThreshold = 0.01m;
    private const decimal MaximumThreshold = 1.00m;

    private readonly DamebooruDbContext _db;

    public AiTaggingSettingsService(DamebooruDbContext db)
    {
        _db = db;
    }

    public async Task<AiTaggingSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateEntityAsync(cancellationToken);
        return ToDto(settings);
    }

    public async Task<Result<AiTaggingSettingsDto>> UpdateAsync(AiTaggingSettingsDto dto, CancellationToken cancellationToken = default)
    {
        var validationError = Validate(dto);
        if (validationError != null)
        {
            return Result<AiTaggingSettingsDto>.Failure(OperationError.InvalidInput, validationError);
        }

        var settings = await GetOrCreateEntityAsync(cancellationToken);
        settings.SuggestionThreshold = dto.SuggestionThreshold;
        settings.ApplyThreshold = dto.ApplyThreshold;

        await _db.SaveChangesAsync(cancellationToken);
        return Result<AiTaggingSettingsDto>.Success(ToDto(settings));
    }

    internal async Task<AiTaggingSettings> GetOrCreateEntityAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.AiTaggingSettings.FirstOrDefaultAsync(s => s.Id == SettingsId, cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new AiTaggingSettings { Id = SettingsId };
        _db.AiTaggingSettings.Add(settings);
        await _db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static string? Validate(AiTaggingSettingsDto dto)
    {
        if (!IsValidThreshold(dto.SuggestionThreshold))
        {
            return $"AI suggestion threshold must be between {MinimumThreshold} and {MaximumThreshold}.";
        }

        if (!IsValidThreshold(dto.ApplyThreshold))
        {
            return $"AI apply threshold must be between {MinimumThreshold} and {MaximumThreshold}.";
        }

        if (dto.ApplyThreshold < dto.SuggestionThreshold)
        {
            return "AI apply threshold must be greater than or equal to the suggestion threshold.";
        }

        return null;
    }

    private static bool IsValidThreshold(decimal value)
        => value is >= MinimumThreshold and <= MaximumThreshold;

    private static AiTaggingSettingsDto ToDto(AiTaggingSettings settings)
        => new()
        {
            SuggestionThreshold = settings.SuggestionThreshold,
            ApplyThreshold = settings.ApplyThreshold
        };
}
