using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class AutoTagDiscoverySettingsService
{
    private const int SettingsId = 1;
    private readonly DamebooruDbContext _db;

    public AutoTagDiscoverySettingsService(DamebooruDbContext db)
    {
        _db = db;
    }

    public async Task<AutoTagDiscoverySettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateEntityAsync(cancellationToken);
        return ToDto(settings);
    }

    public async Task<Result<AutoTagDiscoverySettingsDto>> UpdateAsync(AutoTagDiscoverySettingsDto dto, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateEntityAsync(cancellationToken);

        settings.SauceNaoEnabled = dto.SauceNaoEnabled;
        settings.IqdbEnabled = dto.IqdbEnabled;
        settings.DanbooruEnabled = dto.DanbooruEnabled;
        settings.GelbooruEnabled = dto.GelbooruEnabled;

        await _db.SaveChangesAsync(cancellationToken);
        return Result<AutoTagDiscoverySettingsDto>.Success(ToDto(settings));
    }

    public async Task<AutoTagProvider[]> GetEnabledDiscoveryProvidersAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateEntityAsync(cancellationToken);
        return AutoTagDiscoveryPlan.OrderedDiscoveryProviders
            .Where(provider => IsEnabled(settings, provider))
            .ToArray();
    }

    private async Task<AutoTagDiscoverySettings> GetOrCreateEntityAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.AutoTagDiscoverySettings.FirstOrDefaultAsync(s => s.Id == SettingsId, cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new AutoTagDiscoverySettings { Id = SettingsId };
        _db.AutoTagDiscoverySettings.Add(settings);
        await _db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static bool IsEnabled(AutoTagDiscoverySettings settings, AutoTagProvider provider)
        => provider switch
        {
            AutoTagProvider.SauceNao => settings.SauceNaoEnabled,
            AutoTagProvider.Iqdb => settings.IqdbEnabled,
            AutoTagProvider.Danbooru => settings.DanbooruEnabled,
            AutoTagProvider.Gelbooru => settings.GelbooruEnabled,
            _ => false
        };

    private static AutoTagDiscoverySettingsDto ToDto(AutoTagDiscoverySettings settings)
        => new()
        {
            SauceNaoEnabled = settings.SauceNaoEnabled,
            IqdbEnabled = settings.IqdbEnabled,
            DanbooruEnabled = settings.DanbooruEnabled,
            GelbooruEnabled = settings.GelbooruEnabled
        };
}
