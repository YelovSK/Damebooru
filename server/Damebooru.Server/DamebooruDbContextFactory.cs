using Damebooru.Core.Config;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Damebooru.Server;

public class DamebooruDbContextFactory : IDesignTimeDbContextFactory<DamebooruDbContext>
{
    public DamebooruDbContext CreateDbContext(string[] args)
    {
        var contentRoot = ResolveContentRoot();

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var damebooruConfig = configuration.GetSection(DamebooruConfig.SectionName).Get<DamebooruConfig>() ?? new DamebooruConfig();

        var resolvedConnectionString = StoragePathResolver.ResolveSqliteConnectionString(
            contentRoot,
            configuration.GetConnectionString("DefaultConnection"),
            damebooruConfig.Storage.DatabasePath);

        var builder = new DbContextOptionsBuilder<DamebooruDbContext>();
        builder.UseSqlite(resolvedConnectionString);

        return new DamebooruDbContext(builder.Options);
    }

    private static string ResolveContentRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("DAMEBOORU_CONTENT_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return Path.GetFullPath(explicitRoot);

        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."),
        };

        foreach (var candidate in candidates)
        {
            var root = FindServerProjectRoot(candidate);
            if (root != null)
                return root;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? FindServerProjectRoot(string startPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current != null)
        {
            var hasCsproj = File.Exists(Path.Combine(current.FullName, "Damebooru.Server.csproj"));
            var hasAppSettings = File.Exists(Path.Combine(current.FullName, "appsettings.json"));
            if (hasCsproj && hasAppSettings)
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }
}
