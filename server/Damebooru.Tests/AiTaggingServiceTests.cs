using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.AiTagging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class AiTaggingServiceTests
{
    [Fact]
    public async Task ApplyAsync_TagsFirstFileOfImagePost()
    {
        var libraryPath = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(libraryPath, "a.png"), [1]);

            await using var db = await CreateContextAsync();
            var library = new Library { Name = "Library", Path = libraryPath };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var post = new Post
            {
                ImportDate = DateTime.UtcNow,
                ContentHash = "hash",
                ContentType = "image/png",
                PostFiles =
                [
                    new PostFile { LibraryId = library.Id, RelativePath = "a.png" },
                    new PostFile { LibraryId = library.Id, RelativePath = "missing.png" },
                ],
            };
            db.Posts.Add(post);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var client = new FakeAiTaggingClient();
            var service = new AiTaggingService(
                db,
                new DamebooruConfig { AiTagging = { Enabled = true } },
                client,
                new PostReadService(db),
                new AiTaggingSettingsService(db));

            var result = await service.ApplyAsync(post.Id);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("a.png", client.TaggedFileName);
            Assert.True(await db.PostTags.AnyAsync(pt => pt.PostId == post.Id && pt.Tag.Name == "ai_tag"));
        }
        finally
        {
            Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_ReplacesPredictionsOnEveryRun()
    {
        var libraryPath = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(libraryPath, "a.png"), [1]);

            await using var db = await CreateContextAsync();
            var library = new Library { Name = "Library", Path = libraryPath };
            var post = new Post
            {
                ImportDate = DateTime.UtcNow,
                ContentHash = "hash",
                ContentType = "image/png",
                PostFiles = [new PostFile { Library = library, RelativePath = "a.png" }],
            };
            db.Posts.Add(post);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var client = new FakeAiTaggingClient
            {
                Tags =
                [
                    new AiTagData("Rem_(Re:Zero)", 0.6m, TagCategoryKind.Character, "character"),
                    new AiTagData("some_artist", 0.5m, TagCategoryKind.Artist, "artist"),
                    new AiTagData("1girl", 0.99m, TagCategoryKind.General, "general"),
                ],
            };
            var service = new AiTaggingService(
                db,
                new DamebooruConfig { AiTagging = { Enabled = true } },
                client,
                new PostReadService(db),
                new AiTaggingSettingsService(db));

            Assert.True((await service.ApplyAsync(post.Id)).IsSuccess);
            client.Tags = [new AiTagData("rem_(re:zero)", 0.8m, TagCategoryKind.Character, "character")];
            Assert.True((await service.ApplyAsync(post.Id)).IsSuccess);

            var prediction = Assert.Single(await db.PostAiPredictions.AsNoTracking().ToListAsync());
            Assert.Equal("rem_(re_zero)", prediction.TagName);
            Assert.Equal(0.8m, prediction.Score);
        }
        finally
        {
            Directory.Delete(libraryPath, recursive: true);
        }
    }

    private static async Task<DamebooruDbContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new DamebooruDbContext(new DbContextOptionsBuilder<DamebooruDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class FakeAiTaggingClient : IAiTaggingClient
    {
        public string? TaggedFileName { get; private set; }
        public List<AiTagData> Tags { get; set; } = [new AiTagData("ai_tag", 0.99m, TagCategoryKind.General, "general")];

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<AiTaggingResult> TagAsync(
            Stream fileStream,
            string fileName,
            string? contentType = null,
            decimal? threshold = null,
            decimal? minConfidence = null,
            int? topK = null,
            CancellationToken cancellationToken = default)
        {
            TaggedFileName = fileName;
            return Task.FromResult(new AiTaggingResult(
                "model",
                "cpu",
                threshold ?? 0,
                0.01m,
                1,
                Tags));
        }
    }
}
