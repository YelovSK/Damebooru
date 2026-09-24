using System.Collections.Concurrent;
using System.Threading.Channels;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Processing.Scanning;
using Damebooru.Processing.Services.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public sealed class LibraryWatchSessionTests : IDisposable
{
    private readonly string _libraryPath = Path.Combine(Path.GetTempPath(), "damebooru-watch-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingSyncProcessor _processor = new();
    private readonly Channel<LibraryWatchEvent> _channel = Channel.CreateUnbounded<LibraryWatchEvent>();
    private long _sequence;

    public LibraryWatchSessionTests()
    {
        Directory.CreateDirectory(_libraryPath);
    }

    [Fact]
    public async Task ProcessAsync_KeepsProcessingAfterAnOperationFails()
    {
        WriteFile("bad.png");
        WriteFile("good.png");
        _processor.FailingPath = "bad.png";
        var processing = StartSession();

        Queue(LibraryWatchEventKind.Upsert, "bad.png");
        await WaitUntilAsync(() => _processor.Attempted.Contains("bad.png"));
        Queue(LibraryWatchEventKind.Upsert, "good.png");
        await WaitUntilAsync(() => _processor.Changed.Contains("good.png"));

        await StopAsync(processing);
    }

    private Task StartSession()
    {
        var library = new LibraryWatchTarget(1, "Library", _libraryPath);
        var session = new LibraryWatchSession(
            library,
            _processor,
            new PlatformFileIdentityResolver(NullLogger<PlatformFileIdentityResolver>.Instance),
            new LibraryWatchTrackedStateReader(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()),
            NullLogger<LibraryWatchSession>.Instance,
            debounceDelay: TimeSpan.FromMilliseconds(50),
            deleteGracePeriod: TimeSpan.FromMilliseconds(200));
        return session.ProcessAsync(_channel.Reader, CancellationToken.None);
    }

    private void Queue(LibraryWatchEventKind kind, string relativePath)
        => _channel.Writer.TryWrite(new LibraryWatchEvent(kind, relativePath, null, false, Interlocked.Increment(ref _sequence)));

    private async Task StopAsync(Task processing)
    {
        _channel.Writer.Complete();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private void WriteFile(string relativePath)
    {
        var path = Path.Combine(_libraryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relativePath);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the watcher session.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_libraryPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class RecordingSyncProcessor : ILibrarySyncProcessor
    {
        public string? FailingPath { get; set; }
        public ConcurrentBag<string> Attempted { get; } = [];
        public ConcurrentBag<string> Changed { get; } = [];

        public Task ProcessChangedFileAsync(Library library, MediaSourceItem item, CancellationToken cancellationToken)
        {
            Attempted.Add(item.RelativePath);
            if (item.RelativePath == FailingPath)
            {
                throw new IOException("simulated failure");
            }

            Changed.Add(item.RelativePath);
            return Task.CompletedTask;
        }

        public Task ProcessDeletedFileAsync(Library library, string relativePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessDeletedDirectoryAsync(Library library, string relativePathPrefix, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMovedFileAsync(Library library, string oldRelativePath, MediaSourceItem item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMovedDirectoryAsync(Library library, string oldRelativePathPrefix, string newRelativePathPrefix, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ScanResult> ProcessDirectoryAsync(Library library, string directoryPath, IProgress<float>? progress = null, IProgress<string>? status = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ScanResult.Empty);
    }
}
