using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services.AutoTagging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public sealed class AutoTagScanServiceTests : IDisposable
{
    private readonly string _libraryPath = Path.Combine(Path.GetTempPath(), "damebooru-autotag-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanPostAsync_PermanentFailure_MovesOnAndIsNotRetriedLater()
    {
        await using var db = await CreateContextAsync();
        var postId = await SeedPostAsync(db);
        var sauceNao = new FakeDiscoveryClient(AutoTagProvider.SauceNao, new ExternalProviderException(AutoTagProvider.SauceNao, "Image too small.", rejectsImage: true));
        var iqdb = new FakeDiscoveryClient(AutoTagProvider.Iqdb);
        var service = CreateService(db, sauceNao, iqdb);

        await service.ScanPostAsync(postId);
        await service.ScanPostAsync(postId);

        Assert.Equal(1, sauceNao.Calls);
        Assert.Equal(1, iqdb.Calls);
        var sauceNaoStep = await db.PostAutoTagScanSteps.SingleAsync(step => step.Provider == AutoTagProvider.SauceNao && step.Kind == AutoTagScanStepKind.Discovery);
        Assert.Equal(AutoTagScanStepStatus.PermanentFailure, sauceNaoStep.Status);
        Assert.Equal(1, sauceNaoStep.AttemptCount);
    }

    [Theory]
    [MemberData(nameof(RetryableFailures))]
    public async Task ScanPostAsync_FailuresOtherThanImageRejection_AreRetryable(Exception failure)
    {
        await using var db = await CreateContextAsync();
        var postId = await SeedPostAsync(db);
        var service = CreateService(db, new FakeDiscoveryClient(AutoTagProvider.SauceNao, failure));

        await service.ScanPostAsync(postId);

        var step = await db.PostAutoTagScanSteps.SingleAsync(step => step.Provider == AutoTagProvider.SauceNao && step.Kind == AutoTagScanStepKind.Discovery);
        Assert.Equal(AutoTagScanStepStatus.RetryableFailure, step.Status);
        Assert.NotNull(step.NextRetryAtUtc);
    }

    public static TheoryData<Exception> RetryableFailures() => new()
    {
        new ExternalProviderException(AutoTagProvider.SauceNao, "HTTP 502"),
        new System.Text.Json.JsonException("'<' is an invalid start of a value."),
        new InvalidOperationException("Unexpected bug."),
    };

    private static AutoTagScanService CreateService(DamebooruDbContext db, params FakeDiscoveryClient[] clients)
    {
        IExternalPostDiscoveryClient[] discovery =
        [
            .. clients,
            new FakeDiscoveryClient(AutoTagProvider.Danbooru),
            new FakeDiscoveryClient(AutoTagProvider.Gelbooru),
        ];
        IExternalPostMetadataClient[] metadata =
        [
            new FakeMetadataClient(AutoTagProvider.Danbooru),
            new FakeMetadataClient(AutoTagProvider.Gelbooru),
        ];

        return new AutoTagScanService(
            db,
            discovery,
            metadata,
            new AutoTagDiscoverySettingsService(db),
            new DamebooruConfig(),
            NullLogger<AutoTagScanService>.Instance);
    }

    private async Task<int> SeedPostAsync(DamebooruDbContext db)
    {
        Directory.CreateDirectory(_libraryPath);
        await File.WriteAllBytesAsync(Path.Combine(_libraryPath, "a.png"), [1, 2, 3]);

        var library = new Library { Name = "Library", Path = _libraryPath };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var post = new Post
        {
            ImportDate = DateTime.UtcNow,
            ContentHash = "hash",
            ContentType = "image/png",
            PostFiles = [new PostFile { LibraryId = library.Id, RelativePath = "a.png" }],
        };
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post.Id;
    }

    private static async Task<DamebooruDbContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new DamebooruDbContext(new DbContextOptionsBuilder<DamebooruDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();
        return context;
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

    private sealed class FakeDiscoveryClient(AutoTagProvider provider, Exception? failure = null) : IExternalPostDiscoveryClient
    {
        public int Calls { get; private set; }
        public AutoTagProvider Provider => provider;

        public Task<ExternalDiscoveryResult> DiscoverAsync(PostDiscoveryContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (failure != null)
            {
                throw failure;
            }

            return Task.FromResult(new ExternalDiscoveryResult(provider, []));
        }
    }

    private sealed class FakeMetadataClient(AutoTagProvider provider) : IExternalPostMetadataClient
    {
        public AutoTagProvider Provider => provider;
        public ExternalPostReference? TryParseReference(string url, decimal score) => null;
        public Task<ExternalPostDetails?> GetPostDetailsAsync(long postId, CancellationToken cancellationToken = default)
            => Task.FromResult<ExternalPostDetails?>(null);
    }
}
