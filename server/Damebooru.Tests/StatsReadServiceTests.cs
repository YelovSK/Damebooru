using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class StatsReadServiceTests
{
    [Fact]
    public async Task CollectionStats_ExcludeInboxOnlyPosts()
    {
        await using var db = await CreateContextAsync();
        var library = new Library { Name = "Library", Path = "A:\\library" };
        var inbox = new Library { Name = "Inbox", Path = "A:\\inbox", Kind = LibraryKind.Inbox };
        var tag = new Tag { Name = "tag" };
        AddPost(db, "sorted", 100, [tag], library);
        AddPost(db, "unsorted", 1000, [], inbox);
        AddPost(db, "both", 10, [], library, inbox);
        await db.SaveChangesAsync();
        var service = new StatsReadService(db);

        var overview = await service.GetOverviewAsync();
        var tags = await service.GetTagsAsync();
        var growth = await service.GetGrowthAsync(StatsGrowthDateKind.Imported);
        var storage = await service.GetStorageAsync();

        Assert.Equal(2, overview.PostCount);
        Assert.Equal(1, overview.UnsortedPostCount);
        Assert.Equal(2, overview.FileCount);
        Assert.Equal(110, overview.TotalSizeBytes);
        Assert.Equal(1, overview.UntaggedPostCount);
        Assert.Equal(1, tags.TaggedPostCount);
        Assert.Equal(1, tags.UntaggedPostCount);
        Assert.Equal(2, growth.CumulativePosts[^1].Value);
        Assert.Equal(110, growth.CumulativeSizeBytes[^1].Value);
        Assert.Equal(4, storage.FileCount);
        Assert.Equal(1120, storage.TotalSizeBytes);
    }

    private static void AddPost(DamebooruDbContext db, string contentHash, long sizeBytes, Tag[] tags, params Library[] libraries)
    {
        var date = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        var post = new Post
        {
            ImportDate = date,
            FileModifiedDate = date,
            ContentHash = contentHash,
            ContentType = "image/png",
            SizeBytes = sizeBytes,
        };
        foreach (var library in libraries)
        {
            post.PostFiles.Add(new PostFile
            {
                Library = library,
                RelativePath = $"{contentHash}.png",
                FileModifiedDate = date,
            });
        }

        foreach (var tag in tags)
        {
            post.PostTags.Add(new PostTag { Tag = tag, Source = PostTagSource.Manual });
        }

        db.Posts.Add(post);
    }

    private static async Task<DamebooruDbContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DamebooruDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new DamebooruDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }
}
