namespace Damebooru.Core.Config;

public class DamebooruConfig
{
    public const string SectionName = "Damebooru";

    public StorageConfig Storage { get; set; } = new();
    public ScannerConfig Scanner { get; set; } = new();
    public ProcessingConfig Processing { get; set; } = new();
    public IngestionConfig Ingestion { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public ExternalApisConfig ExternalApis { get; set; } = new();
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

public class ProxyConfig
{
    public bool TrustForwardedHeaders { get; set; } = false;
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

public class ExternalApisConfig
{
    public SauceNaoApiConfig SauceNao { get; set; } = new();
    public DanbooruApiConfig Danbooru { get; set; } = new();
    public GelbooruApiConfig Gelbooru { get; set; } = new();
    public IqdbApiConfig Iqdb { get; set; } = new();
}

public class ExternalApiClientConfig
{
    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public string UserAgent { get; set; } = "Damebooru/1.0";
}

public sealed class SauceNaoApiConfig : ExternalApiClientConfig
{
    public SauceNaoApiConfig()
    {
        BaseUrl = "https://saucenao.com";
    }

    public string ApiKey { get; set; } = string.Empty;
    public int ResultsCount { get; set; } = 10;
    public int Database { get; set; } = 999;
    public decimal MinimumSimilarity { get; set; } = 60m;
}

public sealed class DanbooruApiConfig : ExternalApiClientConfig
{
    public DanbooruApiConfig()
    {
        BaseUrl = "https://danbooru.donmai.us";
    }

    public string Username { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class GelbooruApiConfig : ExternalApiClientConfig
{
    public GelbooruApiConfig()
    {
        BaseUrl = "https://gelbooru.com";
    }

    public string UserId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class IqdbApiConfig : ExternalApiClientConfig
{
    public IqdbApiConfig()
    {
        BaseUrl = "https://iqdb.org";
    }

    public decimal MinimumSimilarity { get; set; } = 75m;
}
