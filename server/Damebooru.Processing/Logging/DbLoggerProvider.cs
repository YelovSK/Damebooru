using Damebooru.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Damebooru.Processing.Logging;

public sealed class DbLoggerProvider : ILoggerProvider
{
    private readonly AppLogChannel _channel;
    private readonly LogLevel _minimumLevel;

    public DbLoggerProvider(AppLogChannel channel, IOptions<DamebooruConfig> options)
    {
        _channel = channel;
        _minimumLevel = ResolveMinimumLevel(options.Value.Logging.Db.MinimumLevel);
    }

    public ILogger CreateLogger(string categoryName)
        => new DbLogger(categoryName, _channel, _minimumLevel);

    public void Dispose()
    {
    }

    private static LogLevel ResolveMinimumLevel(string configuredLevel)
    {
        return Enum.TryParse<LogLevel>(configuredLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Warning;
    }

    private sealed class DbLogger : ILogger
    {
        private static readonly IDisposable NoopScope = new NoopDisposable();

        private readonly string _categoryName;
        private readonly AppLogChannel _channel;
        private readonly LogLevel _minimumLevel;

        public DbLogger(string categoryName, AppLogChannel channel, LogLevel minimumLevel)
        {
            _categoryName = categoryName;
            _channel = channel;
            _minimumLevel = minimumLevel;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NoopScope;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && logLevel >= _minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || AppLogCaptureContext.IsSuppressed)
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
            {
                return;
            }

            ExtractTemplateAndProperties(state, out var template, out var propertiesJson);

            _channel.TryWrite(new AppLogWriteEntry(
                TimestampUtc: DateTime.UtcNow,
                Level: logLevel.ToString(),
                Category: _categoryName,
                Message: string.IsNullOrWhiteSpace(message) ? exception?.Message ?? string.Empty : message,
                Exception: exception?.ToString(),
                MessageTemplate: template,
                PropertiesJson: propertiesJson));
        }

        private static void ExtractTemplateAndProperties<TState>(TState state, out string? messageTemplate, out string? propertiesJson)
        {
            messageTemplate = null;
            propertiesJson = null;

            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                return;
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal)
                    || string.Equals(pair.Key, "OriginalFormat", StringComparison.Ordinal))
                {
                    messageTemplate = pair.Value?.ToString();
                    continue;
                }

                dict[pair.Key] = pair.Value;
            }

            if (dict.Count == 0)
            {
                return;
            }

            propertiesJson = JsonSerializer.Serialize(dict);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
