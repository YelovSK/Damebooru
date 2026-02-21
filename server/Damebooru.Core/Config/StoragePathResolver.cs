using System.Data.Common;

namespace Damebooru.Core.Config;

public static class StoragePathResolver
{
    public static string ResolvePath(string contentRootPath, string? configuredPath, string fallbackRelativePath)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath) ? fallbackRelativePath : configuredPath.Trim();

        if (Path.IsPathRooted(value))
            return Path.GetFullPath(value);

        return Path.GetFullPath(Path.Combine(contentRootPath, value));
    }

    public static string ResolveSqliteConnectionString(
        string contentRootPath,
        string? connectionString,
        string? configuredDatabasePath,
        string fallbackRelativePath = "../damebooru.db")
    {
        // Prefer Damebooru:Storage:DatabasePath as the single mutable storage setting.
        if (!string.IsNullOrWhiteSpace(configuredDatabasePath))
        {
            var resolvedDbPath = ResolvePath(contentRootPath, configuredDatabasePath, fallbackRelativePath);
            return $"Data Source={resolvedDbPath}";
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var resolvedDbPath = ResolvePath(contentRootPath, null, fallbackRelativePath);
            return $"Data Source={resolvedDbPath}";
        }

        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        var dataSourceKey = builder.Keys
            .Cast<string>()
            .FirstOrDefault(k =>
                string.Equals(k, "Data Source", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "Filename", StringComparison.OrdinalIgnoreCase));

        if (dataSourceKey == null || builder[dataSourceKey] is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
            return connectionString;

        var value = rawValue.Trim();
        if (string.Equals(value, ":memory:", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var resolved = ResolvePath(contentRootPath, value, fallbackRelativePath);
        builder[dataSourceKey] = resolved;
        return builder.ConnectionString;
    }
}
