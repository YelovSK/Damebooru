using System.Threading.Channels;

namespace Damebooru.Processing.Logging;

public sealed class AppLogChannel
{
    private readonly Channel<AppLogWriteEntry> _channel;

    public AppLogChannel(int capacity)
    {
        _channel = Channel.CreateBounded<AppLogWriteEntry>(new BoundedChannelOptions(Math.Max(100, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
    }

    public bool TryWrite(AppLogWriteEntry entry) => _channel.Writer.TryWrite(entry);

    public ChannelReader<AppLogWriteEntry> Reader => _channel.Reader;
}

public sealed record AppLogWriteEntry(
    DateTime TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? Exception,
    string? MessageTemplate,
    string? PropertiesJson);
