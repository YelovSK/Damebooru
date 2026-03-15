using Damebooru.Core.Config;
using Damebooru.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Damebooru.Tests;

internal static class ExternalApiTestClientFactory
{
    public static T CreateClient<T>(DamebooruConfig config)
        where T : notnull
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddDamebooruProcessing(config, options => options.EnableScheduler = false);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<T>();
    }
}
