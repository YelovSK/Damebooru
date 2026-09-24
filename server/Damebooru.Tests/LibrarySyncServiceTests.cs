using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Damebooru.Processing.Infrastructure;
using Damebooru.Processing.Scanning;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.Scanning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Damebooru.Tests;

public sealed class LibrarySyncServiceTests
{
    [Fact]
    public async Task Scan_ImportsNewFiles_AndGroupsIdenticalContentIntoOnePost()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "one");
        h.WriteFile("b.png", "two");
        h.WriteFile("copy/a.png", "one");

        var result = await h.ScanAsync();

        Assert.Equal(3, result.Added);
        var files = await h.GetFilesAsync();
        Assert.Equal(3, files.Count);
        Assert.Equal(2, files.Select(f => f.PostId).Distinct().Count());
        Assert.Equal(files.Single(f => f.RelativePath == "a.png").PostId, files.Single(f => f.RelativePath == "copy/a.png").PostId);
    }

    [Fact]
    public async Task Scan_RemovesDeletedFile_AndDeletesPostOnlyWhenItHasNoFilesLeft()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("solo.png", "solo");
        h.WriteFile("dup1.png", "dup");
        h.WriteFile("dup2.png", "dup");
        await h.ScanAsync();

        h.DeleteFile("solo.png");
        h.DeleteFile("dup1.png");
        var result = await h.ScanAsync();

        Assert.Equal(2, result.Removed);
        var files = await h.GetFilesAsync();
        Assert.Equal(["dup2.png"], files.Select(f => f.RelativePath));
        Assert.Equal(1, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Scan_DetectsMove_AndKeepsPost()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("old/a.png", "content");
        await h.ScanAsync();
        var postId = (await h.GetFilesAsync()).Single().PostId;

        h.MoveFile("old/a.png", "new/b.png");
        var result = await h.ScanAsync();

        Assert.Equal(1, result.Moved);
        var file = (await h.GetFilesAsync()).Single();
        Assert.Equal("new/b.png", file.RelativePath);
        Assert.Equal(postId, file.PostId);
        Assert.Equal(h.FolderTagsFor("new/b.png"), await h.GetFolderTagsAsync(postId));
    }

    [Fact]
    public async Task Scan_MoveWithContentChange_JoinsPostMatchingNewContent()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "a content");
        h.WriteFile("b.png", "b content, different");
        await h.ScanAsync();
        await h.SetDimensionsAsync("a.png", 100, 100);
        var bPostId = (await h.GetFilesAsync()).Single(f => f.RelativePath == "b.png").PostId;

        h.MoveFile("a.png", "moved/a.png");
        h.WriteFile("moved/a.png", "b content, different");
        await h.ScanAsync();

        var moved = (await h.GetFilesAsync()).Single(f => f.RelativePath == "moved/a.png");
        Assert.Equal(bPostId, moved.PostId);
        Assert.Equal(0, moved.Width);
        Assert.Equal(1, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Scan_ContentChange_UpdatesHashAndResetsMetadata()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "before");
        await h.ScanAsync();
        await h.SetDimensionsAsync("a.png", 100, 100);
        var postId = (await h.GetFilesAsync()).Single().PostId;

        h.WriteFile("a.png", "after, and longer");
        var result = await h.ScanAsync();

        Assert.Equal(1, result.Updated);
        var file = (await h.GetFilesAsync()).Single();
        Assert.Equal(await h.HashAsync("a.png"), file.ContentHash);
        Assert.Equal(0, file.Width);
        Assert.Equal(postId, file.PostId);
    }

    [Fact]
    public async Task Scan_ContentChangeOfOneCopy_SplitsItIntoNewPost()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "same");
        h.WriteFile("b.png", "same");
        await h.ScanAsync();
        var postId = (await h.GetFilesAsync()).First().PostId;

        h.WriteFile("b.png", "edited, and longer");
        await h.ScanAsync();

        var files = await h.GetFilesAsync();
        Assert.Equal(postId, files.Single(f => f.RelativePath == "a.png").PostId);
        Assert.NotEqual(postId, files.Single(f => f.RelativePath == "b.png").PostId);
    }

    [Fact]
    public async Task Scan_ContentChangeToExistingContent_JoinsThatPost()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "a content");
        h.WriteFile("b.png", "b content, different");
        await h.ScanAsync();
        var bPostId = (await h.GetFilesAsync()).Single(f => f.RelativePath == "b.png").PostId;

        h.WriteFile("a.png", "b content, different");
        await h.ScanAsync();

        var files = await h.GetFilesAsync();
        Assert.All(files, f => Assert.Equal(bPostId, f.PostId));
        Assert.Equal(1, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Scan_SkipsIgnoredPaths_AndExcludedFilesWithMatchingHash()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("ignored/a.png", "a");
        h.WriteFile("excluded.png", "excluded");
        h.WriteFile("kept.png", "kept");
        await h.AddIgnoredPathAsync("ignored");
        await h.AddExcludedFileAsync("excluded.png", await h.HashAsync("excluded.png"));

        await h.ScanAsync();

        Assert.Equal(["kept.png"], (await h.GetFilesAsync()).Select(f => f.RelativePath));
    }

    [Fact]
    public async Task Scan_AppliesFolderTags()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("artist name/series/a.png", "a");

        await h.ScanAsync();

        var postId = (await h.GetFilesAsync()).Single().PostId;
        var expected = h.FolderTagsFor("artist name/series/a.png");
        Assert.NotEmpty(expected);
        Assert.Equal(expected, await h.GetFolderTagsAsync(postId));
    }

    [Fact]
    public async Task Watcher_ChangedFile_CreatesThenUpdatesPost()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("dir/a.png", "first");

        await h.Sync.ProcessChangedFileAsync(h.Library, h.Item("dir/a.png"), CancellationToken.None);

        var file = (await h.GetFilesAsync()).Single();
        Assert.Equal(await h.HashAsync("dir/a.png"), file.ContentHash);
        Assert.Equal(10, file.Width);
        Assert.Equal(h.FolderTagsFor("dir/a.png"), await h.GetFolderTagsAsync(file.PostId));

        h.WriteFile("dir/a.png", "second, longer");
        await h.Sync.ProcessChangedFileAsync(h.Library, h.Item("dir/a.png"), CancellationToken.None);

        file = (await h.GetFilesAsync()).Single();
        Assert.Equal(await h.HashAsync("dir/a.png"), file.ContentHash);
        Assert.Equal(1, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Watcher_ChangedFile_JoinsExistingPostWithSameContent()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "same");
        await h.ScanAsync();
        h.WriteFile("b.png", "same");

        await h.Sync.ProcessChangedFileAsync(h.Library, h.Item("b.png"), CancellationToken.None);

        var files = await h.GetFilesAsync();
        Assert.Equal(2, files.Count);
        Assert.Single(files.Select(f => f.PostId).Distinct());
    }

    [Fact]
    public async Task Watcher_ChangedFile_InExcludedPathIsSkipped_AndRemovesTrackedFile()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "a");
        await h.ScanAsync();
        await h.AddExcludedFileAsync("a.png", await h.HashAsync("a.png"));

        await h.Sync.ProcessChangedFileAsync(h.Library, h.Item("a.png"), CancellationToken.None);

        Assert.Empty(await h.GetFilesAsync());
        Assert.Equal(0, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Watcher_DeletedFile_RemovesFileAndEmptyPost()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "a");
        await h.ScanAsync();

        h.DeleteFile("a.png");
        await h.Sync.ProcessDeletedFileAsync(h.Library, "a.png", CancellationToken.None);

        Assert.Empty(await h.GetFilesAsync());
        Assert.Equal(0, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Watcher_MovedFile_KeepsPostAndUpdatesFolderTags()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("old/a.png", "a");
        await h.ScanAsync();
        var postId = (await h.GetFilesAsync()).Single().PostId;

        h.MoveFile("old/a.png", "new/a.png");
        await h.Sync.ProcessMovedFileAsync(h.Library, "old/a.png", h.Item("new/a.png"), CancellationToken.None);

        var file = (await h.GetFilesAsync()).Single();
        Assert.Equal("new/a.png", file.RelativePath);
        Assert.Equal(postId, file.PostId);
        Assert.Equal(h.FolderTagsFor("new/a.png"), await h.GetFolderTagsAsync(postId));
    }

    [Fact]
    public async Task Watcher_MovedFileOntoTrackedPath_DropsSourceAndUpdatesDestination()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("a.png", "a");
        h.WriteFile("b.png", "b");
        await h.ScanAsync();

        h.DeleteFile("b.png");
        h.MoveFile("a.png", "b.png");
        await h.Sync.ProcessMovedFileAsync(h.Library, "a.png", h.Item("b.png"), CancellationToken.None);

        var file = (await h.GetFilesAsync()).Single();
        Assert.Equal("b.png", file.RelativePath);
        Assert.Equal(await h.HashAsync("b.png"), file.ContentHash);
        Assert.Equal(1, await h.CountPostsAsync());
    }

    [Fact]
    public async Task Watcher_MovedDirectory_RewritesPrefixAndFolderTags()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("old/x/a.png", "a");
        h.WriteFile("old/b.png", "b");
        h.WriteFile("older/c.png", "c");
        await h.ScanAsync();

        h.MoveDirectory("old", "new");
        await h.Sync.ProcessMovedDirectoryAsync(h.Library, "old", "new", CancellationToken.None);

        var files = await h.GetFilesAsync();
        Assert.Equal(["new/b.png", "new/x/a.png", "older/c.png"], files.Select(f => f.RelativePath).Order());
        var moved = files.Single(f => f.RelativePath == "new/x/a.png");
        Assert.Equal(h.FolderTagsFor("new/x/a.png"), await h.GetFolderTagsAsync(moved.PostId));
    }

    [Fact]
    public async Task Watcher_DeletedDirectory_RemovesFilesUnderPrefixOnly()
    {
        await using var h = await SyncHarness.CreateAsync();
        h.WriteFile("gone/a.png", "a");
        h.WriteFile("gone/sub/b.png", "b");
        h.WriteFile("gone2/c.png", "c");
        await h.ScanAsync();

        await h.Sync.ProcessDeletedDirectoryAsync(h.Library, "gone", CancellationToken.None);

        Assert.Equal(["gone2/c.png"], (await h.GetFilesAsync()).Select(f => f.RelativePath));
        Assert.Equal(1, await h.CountPostsAsync());
    }

    private sealed class SyncHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly ContentHasher _hasher = new();

        private SyncHarness(string root, SqliteConnection connection, ServiceProvider provider, Library library)
        {
            _root = root;
            _connection = connection;
            _provider = provider;
            Library = library;
            Sync = provider.GetRequiredService<ILibrarySyncProcessor>();
        }

        public Library Library { get; }
        public ILibrarySyncProcessor Sync { get; }
        private string LibraryPath => Path.Combine(_root, "library");

        public static async Task<SyncHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "damebooru-sync-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "library"));

            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<DamebooruDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IOptions<DamebooruConfig>>(Options.Create(new DamebooruConfig()));
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(root));
            services.AddSingleton<IHasherService, ContentHasher>();
            services.AddSingleton<IMediaSource, FileSystemMediaSource>();
            services.AddSingleton<IFileIdentityResolver, PlatformFileIdentityResolver>();
            services.AddSingleton<IMediaFileProcessor, StubMediaFileProcessor>();
            services.AddSingleton<ISimilarityService, StubSimilarityService>();
            services.AddSingleton<MediaEnrichmentService>();
            services.AddTransient<FolderTaggingService>();
            services.AddSingleton<ILibrarySyncProcessor, LibrarySyncService>();
            var provider = services.BuildServiceProvider();

            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
            await db.Database.MigrateAsync();
            var library = new Library { Name = "Library", Path = Path.Combine(root, "library") };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            return new SyncHarness(root, connection, provider, library);
        }

        public Task<ScanResult> ScanAsync()
            => Sync.ProcessDirectoryAsync(Library, LibraryPath);

        public void WriteFile(string relativePath, string content)
        {
            var path = FullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(content.Length));
        }

        public void DeleteFile(string relativePath) => File.Delete(FullPath(relativePath));

        public void MoveFile(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FullPath(to))!);
            File.Move(FullPath(from), FullPath(to));
        }

        public void MoveDirectory(string from, string to) => Directory.Move(FullPath(from), FullPath(to));

        public MediaSourceItem Item(string relativePath)
        {
            var info = new FileInfo(FullPath(relativePath));
            return new MediaSourceItem
            {
                FullPath = info.FullName,
                RelativePath = relativePath,
                SizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
            };
        }

        public Task<string> HashAsync(string relativePath)
            => _hasher.ComputeContentHashAsync(FullPath(relativePath));

        public List<string> FolderTagsFor(string relativePath)
            => new FolderTaggingService().BuildPlan(relativePath).FolderTags.Order().ToList();

        public Task<List<PostFile>> GetFilesAsync()
            => QueryAsync(db => db.PostFiles.AsNoTracking().OrderBy(f => f.RelativePath).ToListAsync());

        public Task<int> CountPostsAsync()
            => QueryAsync(db => db.Posts.CountAsync());

        public Task<List<string>> GetFolderTagsAsync(int postId)
            => QueryAsync(db => db.PostTags
                .Where(pt => pt.PostId == postId && pt.Source == PostTagSource.Folder)
                .Select(pt => pt.Tag.Name)
                .OrderBy(name => name)
                .ToListAsync());

        public Task SetDimensionsAsync(string relativePath, int width, int height)
            => QueryAsync(db => db.PostFiles
                .Where(f => f.RelativePath == relativePath)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Width, width).SetProperty(f => f.Height, height)));

        public Task AddIgnoredPathAsync(string prefix)
            => QueryAsync(db =>
            {
                db.LibraryIgnoredPaths.Add(new LibraryIgnoredPath { LibraryId = Library.Id, RelativePathPrefix = prefix });
                return db.SaveChangesAsync();
            });

        public Task AddExcludedFileAsync(string relativePath, string hash)
            => QueryAsync(db =>
            {
                db.ExcludedFiles.Add(new ExcludedFile { LibraryId = Library.Id, RelativePath = relativePath, ContentHash = hash, Reason = "test" });
                return db.SaveChangesAsync();
            });

        private async Task<T> QueryAsync<T>(Func<DamebooruDbContext, Task<T>> query)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<DamebooruDbContext>());
        }

        private string FullPath(string relativePath) => Path.Combine(LibraryPath, relativePath);

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
