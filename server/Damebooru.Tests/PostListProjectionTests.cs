using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class PostListProjectionTests
{
    [Fact]
    public async Task GetPostsAsync_ExecutesRepresentativeFileProjection()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedPostWithFilesAsync(db);
        var service = new PostReadService(db);

        var result = await service.GetPostsAsync(null, page: 1, pageSize: 20, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var post = Assert.Single(result.Value!.Items);
        Assert.Equal(seed.PrimaryLibraryId, post.LibraryId);
        Assert.Equal("root/a.png", post.RelativePath);
        Assert.Equal("image/png", post.ContentType);
        Assert.Equal(2, post.PostFiles.Count);
    }

    [Fact]
    public async Task BrowseAsync_ExecutesRepresentativeAndLibraryFileProjection()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedPostWithFilesAsync(db);
        var service = new LibraryBrowseService(db);

        var result = await service.BrowseAsync(
            seed.SecondaryLibraryId,
            "folder",
            recursive: false,
            page: 1,
            pageSize: 20,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var post = Assert.Single(result.Value!.Posts);
        Assert.Equal(seed.SecondaryLibraryId, post.LibraryId);
        Assert.Equal("Library B", post.LibraryName);
        Assert.Equal("folder/b.png", post.RelativePath);
        Assert.Equal(seed.SecondaryLibraryId, post.ThumbnailLibraryId);
        Assert.Equal(2, post.PostFiles.Count);
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

    private static async Task<(int PrimaryLibraryId, int SecondaryLibraryId)> SeedPostWithFilesAsync(DamebooruDbContext db)
    {
        var primaryLibrary = new Library
        {
            Name = "Library A",
            Path = "A:\\library"
        };
        var secondaryLibrary = new Library
        {
            Name = "Library B",
            Path = "B:\\library"
        };
        var post = new Post
        {
            ImportDate = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
            PostFiles =
            {
                new PostFile
                {
                    Library = primaryLibrary,
                    RelativePath = "root/a.png",
                    ContentHash = "hash-a",
                    SizeBytes = 100,
                    Width = 10,
                    Height = 20,
                    ContentType = "image/png",
                    FileModifiedDate = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
                },
                new PostFile
                {
                    Library = secondaryLibrary,
                    RelativePath = "folder/b.png",
                    ContentHash = "hash-b",
                    SizeBytes = 200,
                    Width = 30,
                    Height = 40,
                    ContentType = "image/png",
                    FileModifiedDate = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc),
                }
            }
        };

        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return (primaryLibrary.Id, secondaryLibrary.Id);
    }
}
