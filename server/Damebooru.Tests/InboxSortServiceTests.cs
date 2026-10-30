using Damebooru.Core.Config;
using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Infrastructure;
using Damebooru.Processing.Scanning;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.Files;
using Damebooru.Processing.Services.Scanning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Damebooru.Tests;

public sealed class InboxSortServiceTests
{
    [Fact]
    public async Task Sort_MovesFileIntoLibrary_KeepingPostAndManualTags()
    {
        await using var h = await SortHarness.CreateAsync();
        h.WriteFile(h.Inbox, "downloads/a.png", "a");
        var modifiedUtc = File.GetLastWriteTimeUtc(h.FullPath(h.Inbox, "downloads/a.png"));
        await h.ScanAsync(h.Inbox);
        var before = Assert.Single(await h.GetFilesAsync());
        await h.AddManualTagAsync(before.PostId, "keep_me");

        var result = Assert.Single(await h.SortAsync(before.Id, (h.Library, "Series/Character")));

        Assert.Null(result.Error);
        Assert.Null(result.Warning);
        Assert.False(File.Exists(h.FullPath(h.Inbox, "downloads/a.png")));
        var movedPath = h.FullPath(h.Library, "Series/Character/a.png");
        Assert.Equal("a", File.ReadAllText(movedPath));
        Assert.Equal(modifiedUtc, File.GetLastWriteTimeUtc(movedPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(movedPath)!, "*.tmp"));

        var after = Assert.Single(await h.GetFilesAsync());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.PostId, after.PostId);
        Assert.Equal(h.Library.Id, after.LibraryId);
        Assert.Equal("Series/Character/a.png", after.RelativePath);
        var tags = await h.GetTagsAsync(after.PostId);
        Assert.Contains("keep_me", tags);
        Assert.Equal(2, tags.Count(t => t != "keep_me"));
    }

    [Fact]
    public async Task Sort_WatcherEventsForTheMove_ChangeNothingAndHashNothing()
    {
        await using var h = await SortHarness.CreateAsync();
        h.WriteFile(h.Inbox, "a.png", "a");
        await h.ScanAsync(h.Inbox);
        var before = Assert.Single(await h.GetFilesAsync());
        await h.SortAsync(before.Id, (h.Library, "folder"));
        var hashesBefore = h.Hasher.Count;

        await h.Sync.ProcessChangedFileAsync(h.Library, h.Item(h.Library, "folder/a.png"), CancellationToken.None);
        await h.Sync.ProcessDeletedFileAsync(h.Inbox, "a.png", CancellationToken.None);

        Assert.Equal(hashesBefore, h.Hasher.Count);
        var after = Assert.Single(await h.GetFilesAsync());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("folder/a.png", after.RelativePath);
        Assert.Equal(1, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Sort_CopiesToExtraDestinations_AndSuffixesTakenNames()
    {
        await using var h = await SortHarness.CreateAsync();
        h.WriteFile(h.Library, "x/a.png", "other");
        await h.ScanAsync(h.Library);
        h.WriteFile(h.Inbox, "a.png", "a");
        await h.ScanAsync(h.Inbox);
        var inboxFile = (await h.GetFilesAsync()).Single(f => f.LibraryId == h.Inbox.Id);

        var result = Assert.Single(await h.SortAsync(inboxFile.Id, (h.Library, "x"), (h.Library, "y")));

        Assert.Null(result.Error);
        Assert.Equal(["x/a (2).png", "y/a.png"], result.Locations.Select(l => l.RelativePath).Order());
        Assert.Equal("a", File.ReadAllText(h.FullPath(h.Library, "x/a (2).png")));
        Assert.Equal("a", File.ReadAllText(h.FullPath(h.Library, "y/a.png")));
        var postFiles = (await h.GetFilesAsync()).Where(f => f.PostId == inboxFile.PostId).ToList();
        Assert.Equal(["x/a (2).png", "y/a.png"], postFiles.Select(f => f.RelativePath).Order());
        Assert.All(postFiles, f => Assert.Equal(h.Library.Id, f.LibraryId));
    }

    [Fact]
    public async Task Sort_RejectsFilesOutsideInboxes_AndInboxDestinations()
    {
        await using var h = await SortHarness.CreateAsync();
        h.WriteFile(h.Library, "a.png", "a");
        h.WriteFile(h.Inbox, "b.png", "b");
        await h.ScanAsync(h.Library);
        await h.ScanAsync(h.Inbox);
        var files = await h.GetFilesAsync();
        var libraryFile = files.Single(f => f.LibraryId == h.Library.Id);
        var inboxFile = files.Single(f => f.LibraryId == h.Inbox.Id);

        var fromLibrary = Assert.Single(await h.SortAsync(libraryFile.Id, (h.Library, "x")));
        var intoInbox = Assert.Single(await h.SortAsync(inboxFile.Id, (h.Inbox, "x")));
        var outside = Assert.Single(await h.SortAsync(inboxFile.Id, (h.Library, "../escape")));

        Assert.NotNull(fromLibrary.Error);
        Assert.NotNull(intoInbox.Error);
        Assert.NotNull(outside.Error);
        Assert.True(File.Exists(h.FullPath(h.Inbox, "b.png")));
        Assert.Equal(files.Select(f => (f.Id, f.LibraryId, f.RelativePath)), (await h.GetFilesAsync()).Select(f => (f.Id, f.LibraryId, f.RelativePath)));
    }

    private sealed class SortHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        private SortHarness(string root, SqliteConnection connection, ServiceProvider provider, Library library, Library inbox)
        {
            _root = root;
            _connection = connection;
            _provider = provider;
            Library = library;
            Inbox = inbox;
            Sync = provider.GetRequiredService<ILibrarySyncProcessor>();
            Hasher = (CountingHasher)provider.GetRequiredService<IHasherService>();
        }

        public Library Library { get; }
        public Library Inbox { get; }
        public ILibrarySyncProcessor Sync { get; }
        public CountingHasher Hasher { get; }

        public static async Task<SortHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "damebooru-sort-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "library"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));

            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<DamebooruDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IOptions<DamebooruConfig>>(Options.Create(new DamebooruConfig()));
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(root));
            services.AddSingleton<IHasherService, CountingHasher>();
            services.AddSingleton<IMediaSource, FileSystemMediaSource>();
            services.AddSingleton<IFileIdentityResolver, PlatformFileIdentityResolver>();
            services.AddSingleton<IHardLinkService, PlatformHardLinkService>();
            services.AddSingleton<IMediaFileProcessor, StubMediaFileProcessor>();
            services.AddSingleton<ISimilarityService, StubSimilarityService>();
            services.AddSingleton<IJobService, IdleJobService>();
            services.AddSingleton<MediaEnrichmentService>();
            services.AddTransient<FolderTaggingService>();
            services.AddSingleton<ILibrarySyncProcessor, LibrarySyncService>();
            services.AddScoped<InboxSortService>();
            var provider = services.BuildServiceProvider();

            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
            await db.Database.MigrateAsync();
            var library = new Library { Name = "Library", Path = Path.Combine(root, "library") };
            var inbox = new Library { Name = "Inbox", Path = Path.Combine(root, "inbox"), Kind = LibraryKind.Inbox };
            db.Libraries.AddRange(library, inbox);
            await db.SaveChangesAsync();

            return new SortHarness(root, connection, provider, library, inbox);
        }

        public Task<ScanResult> ScanAsync(Library library)
            => Sync.ProcessDirectoryAsync(library, library.Path);

        public async Task<List<SortInboxFileResultDto>> SortAsync(int postFileId, params (Library Library, string Folder)[] destinations)
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<InboxSortService>();
            var result = await service.SortAsync(new SortInboxFilesDto
            {
                Files =
                [
                    new SortInboxFileDto
                    {
                        PostFileId = postFileId,
                        Destinations = destinations
                            .Select(d => new SortDestinationDto { LibraryId = d.Library.Id, Folder = d.Folder })
                            .ToList(),
                    },
                ],
            }, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Message);
            return result.Value!;
        }

        public void WriteFile(Library library, string relativePath, string content)
        {
            var path = FullPath(library, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 12, 0, content.Length, DateTimeKind.Utc));
        }

        public string FullPath(Library library, string relativePath) => Path.Combine(library.Path, relativePath);

        public MediaSourceItem Item(Library library, string relativePath)
        {
            var info = new FileInfo(FullPath(library, relativePath));
            return new MediaSourceItem
            {
                FullPath = info.FullName,
                RelativePath = relativePath,
                SizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
            };
        }

        public Task<List<PostFile>> GetFilesAsync()
            => QueryAsync(db => db.PostFiles.AsNoTracking().OrderBy(f => f.Id).ToListAsync());

        public Task<int> CountPostsAsync()
            => QueryAsync(db => db.Posts.CountAsync());

        public Task<List<string>> GetTagsAsync(int postId)
            => QueryAsync(db => db.PostTags.Where(pt => pt.PostId == postId).Select(pt => pt.Tag.Name).ToListAsync());

        public Task AddManualTagAsync(int postId, string name)
            => QueryAsync(db =>
            {
                db.PostTags.Add(new PostTag { PostId = postId, Tag = new Tag { Name = name }, Source = PostTagSource.Manual });
                return db.SaveChangesAsync();
            });

        private async Task<T> QueryAsync<T>(Func<DamebooruDbContext, Task<T>> query)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<DamebooruDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class CountingHasher : IHasherService
    {
        private readonly ContentHasher _inner = new();
        private int _count;

        public int Count => _count;

        public Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.ComputeContentHashAsync(filePath, cancellationToken);
        }

        public Task<string> ComputeContentHashAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.ComputeContentHashAsync(stream, cancellationToken);
        }
    }

    private sealed class IdleJobService : IJobService
    {
        public IEnumerable<JobInfo> GetActiveJobs() => [];
        public Task<(List<JobExecution> Items, int Total)> GetJobHistoryAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IEnumerable<JobDefinition> GetAvailableJobs() => [];
        public bool IsRunning(JobKey jobKey) => false;
        public Task<string> StartJobAsync(JobKey jobKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> StartJobAsync(JobKey jobKey, CancellationToken cancellationToken, JobMode mode) => throw new NotSupportedException();
        public Task<string> StartJobAsync(JobKey jobKey, Func<CancellationToken, Task> action) => throw new NotSupportedException();
        public void CancelJob(string jobId) => throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Damebooru.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubMediaFileProcessor : IMediaFileProcessor
    {
        public Task GeneratePreviewAsync(string sourcePath, string destinationPath, int maxSize, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task GenerateThumbnailAsync(string sourcePath, string destinationPath, int size, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<MediaMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new MediaMetadata { Width = 10, Height = 10, Format = "png" });
    }

    private sealed class StubSimilarityService : ISimilarityService
    {
        public Task<SimilarityHashes> ComputeHashesAsync(Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(new SimilarityHashes(new string('0', 64)));

        public Task<SimilarityHashes> ComputeHashesAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new SimilarityHashes(new string('0', 64)));
    }
}
