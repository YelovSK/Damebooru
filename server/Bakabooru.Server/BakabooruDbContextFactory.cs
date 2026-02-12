using Bakabooru.Core.Config;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Bakabooru.Server;

public class BakabooruDbContextFactory : IDesignTimeDbContextFactory<BakabooruDbContext>
{
    public BakabooruDbContext CreateDbContext(string[] args)
    {
        var contentRoot = ResolveContentRoot();

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var bakabooruConfig = configuration.GetSection(BakabooruConfig.SectionName).Get<BakabooruConfig>() ?? new BakabooruConfig();

        var resolvedConnectionString = StoragePathResolver.ResolveSqliteConnectionString(
            contentRoot,
            configuration.GetConnectionString("DefaultConnection"),
            bakabooruConfig.Storage.DatabasePath);

        var builder = new DbContextOptionsBuilder<BakabooruDbContext>();
        builder.UseSqlite(resolvedConnectionString);

        return new BakabooruDbContext(builder.Options);
    }

    private static string ResolveContentRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("BAKABOORU_CONTENT_ROOT");
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
            var hasCsproj = File.Exists(Path.Combine(current.FullName, "Bakabooru.Server.csproj"));
            var hasAppSettings = File.Exists(Path.Combine(current.FullName, "appsettings.json"));
            if (hasCsproj && hasAppSettings)
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }
}
