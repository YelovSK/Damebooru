using System.Data;
using Damebooru.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Server.Infrastructure;

public static class SqliteDatabaseBackup
{
    public static string CreatePreMigrationBackup(DamebooruDbContext db, string sqliteDbPath)
    {
        var dbDirectory = Path.GetDirectoryName(sqliteDbPath);
        if (string.IsNullOrWhiteSpace(dbDirectory))
            dbDirectory = Directory.GetCurrentDirectory();

        var backupDirectory = Path.Combine(dbDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);

        var dbName = Path.GetFileNameWithoutExtension(sqliteDbPath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var backupPath = Path.Combine(backupDirectory, $"{dbName}.pre-migration-{timestamp}.db");

        var sourceConnection = (SqliteConnection)db.Database.GetDbConnection();
        var openedSource = sourceConnection.State != ConnectionState.Open;
        if (openedSource)
            sourceConnection.Open();

        try
        {
            using var destinationConnection = new SqliteConnection(CreateConnectionString(backupPath));
            destinationConnection.Open();
            sourceConnection.BackupDatabase(destinationConnection);
        }
        finally
        {
            if (openedSource)
                sourceConnection.Close();
        }

        VerifyBackup(backupPath);
        return backupPath;
    }

    private static void VerifyBackup(string backupPath)
    {
        using var connection = new SqliteConnection(CreateConnectionString(backupPath, SqliteOpenMode.ReadOnly));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";

        var result = command.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite backup integrity check failed for '{backupPath}': {result}");
    }

    private static string CreateConnectionString(string path, SqliteOpenMode? mode = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path
        };

        if (mode.HasValue)
            builder.Mode = mode.Value;

        return builder.ToString();
    }
}
