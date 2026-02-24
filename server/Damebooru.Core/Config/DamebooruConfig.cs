namespace Damebooru.Core.Config;

public class DamebooruConfig
{
    public const string SectionName = "Damebooru";

    public StorageConfig Storage { get; set; } = new();
    public ScannerConfig Scanner { get; set; } = new();
    public ProcessingConfig Processing { get; set; } = new();
    public IngestionConfig Ingestion { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public class StorageConfig
{
    /// <summary>
    /// Path where the SQLite database is stored.
    /// Default: ./data/damebooru.db
    /// </summary>
    public string DatabasePath { get; set; } = "data/damebooru.db";

    /// <summary>
    /// Path where thumbnails are stored.
    /// Default: ./data/thumbnails
    /// </summary>
    public string ThumbnailPath { get; set; } = "data/thumbnails";

    /// <summary>
    /// Path for temporary files during processing.
    /// Default: ./data/temp
    /// </summary>
    public string TempPath { get; set; } = "data/temp";
}

public class ScannerConfig
{
    public int BatchSize { get; set; } = 100;
    public int Parallelism { get; set; } = 2;
}

public class ProcessingConfig
{
    public bool RunScheduler { get; set; } = true;
    public int MetadataParallelism { get; set; } = 2;
    public int SimilarityParallelism { get; set; } = 2;
    public int ThumbnailParallelism { get; set; } = 2;
    public int JobProgressReportIntervalMs { get; set; } = 1000;
}

public class IngestionConfig
{
    public int BatchSize { get; set; } = 100;
    public int ChannelCapacity { get; set; } = 1000;
}

public class AuthConfig
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "change-me";
}

public class LoggingConfig
{
    public DbLoggingConfig Db { get; set; } = new();
}

public class DbLoggingConfig
{
    public bool Enabled { get; set; } = true;
    public string MinimumLevel { get; set; } = "Warning";
    public int BatchSize { get; set; } = 100;
    public int FlushIntervalMs { get; set; } = 1000;
    public int ChannelCapacity { get; set; } = 10000;
    public int RetentionDays { get; set; } = 7;
    public int MaxRows { get; set; } = 10000;
    public int RetentionCheckIntervalMinutes { get; set; } = 15;
}
