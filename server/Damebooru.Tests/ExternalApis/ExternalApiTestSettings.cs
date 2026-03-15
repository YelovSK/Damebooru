using Damebooru.Core.Config;
using Microsoft.Extensions.Configuration;

namespace Damebooru.Tests;

internal sealed class ExternalApiTestSettings
{
    public required DamebooruConfig Config { get; init; }

    public static ExternalApiTestSettings Load()
    {
        var serverRoot = FindServerRoot();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(serverRoot)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return new ExternalApiTestSettings
        {
            Config = configuration.GetSection(DamebooruConfig.SectionName).Get<DamebooruConfig>() ?? new DamebooruConfig()
        };
    }

    private static string FindServerRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "Damebooru.Server");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Damebooru.Server");
    }
}
