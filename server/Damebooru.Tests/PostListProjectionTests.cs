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

        var result = await service.GetPostsAsync(null, offset: 0, limit: 20, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var post = Assert.Single(result.Value!.Items);
        Assert.Equal(seed.PrimaryLibraryId, post.LibraryId);
        Assert.Equal("root/a.png", post.RelativePath);
        Assert.Equal("image/png", post.ContentType);
        Assert.Equal(2, post.PostFiles.Count);
    }

    [Fact]
    public async Task GetPostsAsync_UsesOffsetAndLimit()
    {
        await using var db = await CreateContextAsync();
        var library = new Library
        {
            Name = "Library A",
            Path = "A:\\library"
        };

        var posts = new List<Post>();
        for (var index = 0; index < 3; index++)
        {
            var seededPost = new Post
            {
                ImportDate = new DateTime(2026, 5, 19, 12, index, 0, DateTimeKind.Utc),
                PrimaryFileModifiedDate = new DateTime(2026, 5, 18 + index, 12, 0, 0, DateTimeKind.Utc),
                PostFiles =
                {
                    new PostFile
                    {
                        Library = library,
                        RelativePath = $"root/{index}.png",
                        ContentHash = $"hash-{index}",
                        SizeBytes = 100 + index,
                        Width = 10,
                        Height = 20,
                        ContentType = "image/png",
                        FileModifiedDate = new DateTime(2026, 5, 18 + index, 12, 0, 0, DateTimeKind.Utc),
                    }
                }
            };
            posts.Add(seededPost);
            db.Posts.Add(seededPost);
        }

        await db.SaveChangesAsync();
        foreach (var seededPost in posts)
        {
            var primaryFile = seededPost.PostFiles.OrderBy(pf => pf.Id).First();
            seededPost.PrimaryPostFileId = primaryFile.Id;
            seededPost.PrimaryFileModifiedDate = primaryFile.FileModifiedDate;
        }
        await db.SaveChangesAsync();
        var service = new PostReadService(db);

        var result = await service.GetPostsAsync(null, offset: 1, limit: 1, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.TotalCount);
        Assert.Equal(1, result.Value.Offset);
        Assert.Equal(1, result.Value.Limit);
        var post = Assert.Single(result.Value.Items);
        Assert.Equal("root/1.png", post.RelativePath);
    }

    [Fact]
    public async Task PrimaryPostFileCacheTriggers_MaintainCachedValues()
    {
        await using var db = await CreateMigratedContextAsync();
        var library = new Library
        {
            Name = "Library A",
            Path = "A:\\library"
        };
        var post = new Post
        {
            ImportDate = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        };

        db.Libraries.Add(library);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        var firstFile = new PostFile
        {
            PostId = post.Id,
            LibraryId = library.Id,
            RelativePath = "root/a.png",
            ContentHash = "hash-a",
            SizeBytes = 100,
            Width = 10,
            Height = 20,
            ContentType = "image/png",
            FileModifiedDate = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
        };
        db.PostFiles.Add(firstFile);
        await db.SaveChangesAsync();
        await db.Entry(post).ReloadAsync();

        Assert.Equal(firstFile.Id, post.PrimaryPostFileId);
        Assert.Equal(firstFile.FileModifiedDate, post.PrimaryFileModifiedDate);

        var secondFile = new PostFile
        {
            PostId = post.Id,
            LibraryId = library.Id,
            RelativePath = "root/b.png",
            ContentHash = "hash-a",
            SizeBytes = 100,
            Width = 10,
            Height = 20,
            ContentType = "image/png",
            FileModifiedDate = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        };
        db.PostFiles.Add(secondFile);
        await db.SaveChangesAsync();
        await db.Entry(post).ReloadAsync();

        Assert.Equal(firstFile.Id, post.PrimaryPostFileId);
        Assert.Equal(firstFile.FileModifiedDate, post.PrimaryFileModifiedDate);

        firstFile.FileModifiedDate = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        await db.Entry(post).ReloadAsync();

        Assert.Equal(firstFile.Id, post.PrimaryPostFileId);
        Assert.Equal(firstFile.FileModifiedDate, post.PrimaryFileModifiedDate);

        db.PostFiles.Remove(firstFile);
        await db.SaveChangesAsync();
        await db.Entry(post).ReloadAsync();

        Assert.Equal(secondFile.Id, post.PrimaryPostFileId);
        Assert.Equal(secondFile.FileModifiedDate, post.PrimaryFileModifiedDate);
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

    private static async Task<DamebooruDbContext> CreateMigratedContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DamebooruDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new DamebooruDbContext(options);
        await context.Database.MigrateAsync();
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
        var primaryFile = post.PostFiles.OrderBy(pf => pf.Id).First();
        post.PrimaryPostFileId = primaryFile.Id;
        post.PrimaryFileModifiedDate = primaryFile.FileModifiedDate;
        await db.SaveChangesAsync();
        return (primaryLibrary.Id, secondaryLibrary.Id);
    }
}
