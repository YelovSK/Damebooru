using Damebooru.Core.Config;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class AutoTagConfigurationValidator
{
    private readonly DamebooruConfig _config;
    private readonly AutoTagDiscoverySettingsService _discoverySettingsService;

    public AutoTagConfigurationValidator(DamebooruConfig config, AutoTagDiscoverySettingsService discoverySettingsService)
    {
        _config = config;
        _discoverySettingsService = discoverySettingsService;
    }

    public async Task EnsureConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();
        var enabledDiscoveryProviders = await _discoverySettingsService.GetEnabledDiscoveryProvidersAsync(cancellationToken);
        if (enabledDiscoveryProviders.Length == 0)
        {
            return;
        }

        if (enabledDiscoveryProviders.Contains(Damebooru.Core.Entities.AutoTagProvider.SauceNao)
            && string.IsNullOrWhiteSpace(_config.ExternalApis.SauceNao.ApiKey))
        {
            missing.Add("Damebooru:ExternalApis:SauceNao:ApiKey");
        }

        if (string.IsNullOrWhiteSpace(_config.ExternalApis.Danbooru.Username))
        {
            missing.Add("Damebooru:ExternalApis:Danbooru:Username");
        }

        if (string.IsNullOrWhiteSpace(_config.ExternalApis.Danbooru.ApiKey))
        {
            missing.Add("Damebooru:ExternalApis:Danbooru:ApiKey");
        }

        if (string.IsNullOrWhiteSpace(_config.ExternalApis.Gelbooru.UserId))
        {
            missing.Add("Damebooru:ExternalApis:Gelbooru:UserId");
        }

        if (string.IsNullOrWhiteSpace(_config.ExternalApis.Gelbooru.ApiKey))
        {
            missing.Add("Damebooru:ExternalApis:Gelbooru:ApiKey");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Auto-tagging is not configured. Missing: {string.Join(", ", missing)}.");
        }
    }
}
