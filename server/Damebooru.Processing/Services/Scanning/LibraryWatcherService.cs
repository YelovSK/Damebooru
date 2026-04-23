using System.Collections.Concurrent;
using System.Threading.Channels;
using Damebooru.Core.Config;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Damebooru.Processing.Services.Scanning;

public sealed class LibraryWatcherService : BackgroundService
{
    private sealed class LibraryWatcherRegistration : IDisposable
    {
        public required LibraryWatchTarget Library { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public required Channel<LibraryWatchEvent> Channel { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; init; }
        public required Task ProcessingTask { get; init; }

        public void Dispose()
        {
            Watcher.Dispose();
            CancellationTokenSource.Dispose();
        }
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LibraryWatchSessionFactory _sessionFactory;
    private readonly ILogger<LibraryWatcherService> _logger;
    private readonly TimeSpan _reloadInterval;
    private readonly ConcurrentDictionary<int, LibraryWatcherRegistration> _registrations = new();
    private long _sequence;

    public LibraryWatcherService(
        IServiceScopeFactory scopeFactory,
        LibraryWatchSessionFactory sessionFactory,
        ILogger<LibraryWatcherService> logger,
        IOptions<DamebooruConfig> options)
    {
        _scopeFactory = scopeFactory;
        _sessionFactory = sessionFactory;
        _logger = logger;
        _reloadInterval = TimeSpan.FromSeconds(Math.Max(5, options.Value.Scanner.WatcherReloadIntervalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshWatchersAsync(stoppingToken);

        using var timer = new PeriodicTimer(_reloadInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshWatchersAsync(stoppingToken);
            }
        }
        finally
        {
            await StopAllAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await StopAllAsync();
    }

    public override void Dispose()
    {
        foreach (var registration in _registrations.Values)
        {
            registration.Dispose();
        }

        base.Dispose();
    }

    private async Task RefreshWatchersAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
        var libraries = await dbContext.Libraries
            .AsNoTracking()
            .Select(l => new LibraryWatchTarget(l.Id, l.Name, Path.GetFullPath(l.Path)))
            .ToListAsync(cancellationToken);

        var seen = new HashSet<int>();
        foreach (var library in libraries)
        {
            seen.Add(library.Id);

            if (!Directory.Exists(library.Path))
            {
                await RemoveRegistrationAsync(library.Id);
                _logger.LogWarning("Skipping file watcher for missing library path {Path} ({Library})", library.Path, library.Name);
                continue;
            }

            if (_registrations.TryGetValue(library.Id, out var existing))
            {
                if (string.Equals(existing.Library.Path, library.Path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Library.Name, library.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                await RemoveRegistrationAsync(library.Id);
            }

            var registration = CreateRegistration(library);
            if (_registrations.TryAdd(library.Id, registration))
            {
                _logger.LogInformation("Watching library {Library} at {Path}", library.Name, library.Path);
            }
            else
            {
                registration.Dispose();
            }
        }

        foreach (var libraryId in _registrations.Keys)
        {
            if (!seen.Contains(libraryId))
            {
                await RemoveRegistrationAsync(libraryId);
            }
        }
    }

    private LibraryWatcherRegistration CreateRegistration(LibraryWatchTarget library)
    {
        var channel = Channel.CreateUnbounded<LibraryWatchEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var watcher = new FileSystemWatcher(library.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };

        var cts = new CancellationTokenSource();
        var session = _sessionFactory.Create(library);
        var registration = new LibraryWatcherRegistration
        {
            Library = library,
            Watcher = watcher,
            Channel = channel,
            CancellationTokenSource = cts,
            ProcessingTask = session.ProcessAsync(channel.Reader, cts.Token),
        };

        watcher.Created += (_, e) => TryQueueCreated(registration, e.FullPath);
        watcher.Changed += (_, e) => TryQueueChanged(registration, e.FullPath);
        watcher.Deleted += (_, e) => TryQueueDelete(registration, e.FullPath);
        watcher.Renamed += (_, e) => TryQueueMove(registration, e.OldFullPath, e.FullPath);
        watcher.Error += (_, e) => TryQueueOverflow(registration, e.GetException());
        watcher.EnableRaisingEvents = true;

        return registration;
    }

    private async Task RemoveRegistrationAsync(int libraryId)
    {
        if (!_registrations.TryRemove(libraryId, out var registration))
        {
            return;
        }

        registration.Watcher.EnableRaisingEvents = false;
        registration.CancellationTokenSource.Cancel();
        registration.Channel.Writer.TryComplete();

        try
        {
            await registration.ProcessingTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            registration.Dispose();
        }

        _logger.LogInformation("Stopped watching library {Library}", registration.Library.Name);
    }

    private async Task StopAllAsync()
    {
        foreach (var libraryId in _registrations.Keys.ToList())
        {
            await RemoveRegistrationAsync(libraryId);
        }
    }

    private void TryQueueCreated(LibraryWatcherRegistration registration, string fullPath)
    {
        if (!LibraryWatchPathHelper.TryGetRelativePath(registration.Library, fullPath, out var relativePath))
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            _logger.LogInformation("Watcher raw create event for library {Library}: {Path} (directory)", registration.Library.Name, relativePath);
            TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Upsert, relativePath, null, true, NextSequence()));
            return;
        }

        if (!LibraryWatchPathHelper.IsSupportedFile(fullPath))
        {
            return;
        }

        _logger.LogDebug("Watcher raw upsert event for library {Library}: {Path}", registration.Library.Name, relativePath);
        TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Upsert, relativePath, null, false, NextSequence()));
    }

    private void TryQueueChanged(LibraryWatcherRegistration registration, string fullPath)
    {
        if (!LibraryWatchPathHelper.TryGetRelativePath(registration.Library, fullPath, out var relativePath)
            || !LibraryWatchPathHelper.IsSupportedFile(fullPath))
        {
            return;
        }

        _logger.LogDebug("Watcher raw upsert event for library {Library}: {Path}", registration.Library.Name, relativePath);
        TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Upsert, relativePath, null, false, NextSequence()));
    }

    private void TryQueueDelete(LibraryWatcherRegistration registration, string fullPath)
    {
        if (!LibraryWatchPathHelper.TryGetRelativePath(registration.Library, fullPath, out var relativePath))
        {
            return;
        }

        _logger.LogDebug("Watcher raw delete event for library {Library}: {Path}", registration.Library.Name, relativePath);
        TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Delete, relativePath, null, false, NextSequence()));
    }

    private void TryQueueMove(LibraryWatcherRegistration registration, string oldFullPath, string newFullPath)
    {
        var oldSupported = LibraryWatchPathHelper.IsSupportedFile(oldFullPath);
        var newSupported = LibraryWatchPathHelper.IsSupportedFile(newFullPath);

        if (!LibraryWatchPathHelper.TryGetRelativePath(registration.Library, oldFullPath, out var oldRelativePath)
            || !LibraryWatchPathHelper.TryGetRelativePath(registration.Library, newFullPath, out var newRelativePath))
        {
            return;
        }

        var isDirectoryMove = Directory.Exists(newFullPath);

        if (isDirectoryMove)
        {
            _logger.LogInformation(
                "Watcher raw move event for library {Library}: {OldPath} -> {NewPath} (directory)",
                registration.Library.Name,
                oldRelativePath,
                newRelativePath);

            TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Move, newRelativePath, oldRelativePath, true, NextSequence()));
            return;
        }

        if (oldSupported && newSupported)
        {
            _logger.LogDebug(
                "Watcher raw move event for library {Library}: {OldPath} -> {NewPath}",
                registration.Library.Name,
                oldRelativePath,
                newRelativePath);

            TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Move, newRelativePath, oldRelativePath, false, NextSequence()));
            return;
        }

        if (oldSupported)
        {
            TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Delete, oldRelativePath, null, false, NextSequence()));
        }

        if (newSupported)
        {
            TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Upsert, newRelativePath, null, false, NextSequence()));
        }
    }

    private void TryQueueOverflow(LibraryWatcherRegistration registration, Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogWarning(exception, "File watcher overflow/error for library {Library}. A later scan may be needed to reconcile missed changes.", registration.Library.Name);
        }
        else
        {
            _logger.LogWarning("File watcher overflow/error for library {Library}. A later scan may be needed to reconcile missed changes.", registration.Library.Name);
        }

        TryQueue(registration, new LibraryWatchEvent(LibraryWatchEventKind.Overflow, string.Empty, null, false, NextSequence()));
    }

    private void TryQueue(LibraryWatcherRegistration registration, LibraryWatchEvent watchEvent)
    {
        if (!registration.Channel.Writer.TryWrite(watchEvent))
        {
            _logger.LogWarning("Failed to queue watcher event for library {Library}", registration.Library.Name);
        }
    }

    private long NextSequence()
        => Interlocked.Increment(ref _sequence);
}
