using Damebooru.Core.Config;

namespace Damebooru.Processing.Services.AutoTagging;

public sealed class AutoTagConfigurationValidator
{
    private readonly DamebooruConfig _config;

    public AutoTagConfigurationValidator(DamebooruConfig config)
    {
        _config = config;
    }

    public void EnsureConfigured()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(_config.ExternalApis.SauceNao.ApiKey))
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
