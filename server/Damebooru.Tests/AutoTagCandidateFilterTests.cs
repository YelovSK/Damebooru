using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services.AutoTagging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class AutoTagCandidateFilterTests
{
    [Fact]
    public async Task ExcludeAutoTagIgnoredPathsAsync_RemovesPostsWithImagesUnderExcludedPrefixes()
    {
        await using var db = await CreateContextAsync();
        var (keptPostId, excludedPostId, nonImagePostId) = await SeedPostsAsync(db);

        var filtered = await AutoTagCandidateFilter.ExcludeAutoTagIgnoredPathsAsync(
            db,
            [keptPostId, excludedPostId, nonImagePostId],
            CancellationToken.None);

        Assert.Equal([keptPostId, nonImagePostId], filtered);
    }

    [Fact]
    public async Task ExcludeAutoTagIgnoredPathsAsync_ReturnsEmptyCandidateListAsIs()
    {
        await using var db = await CreateContextAsync();

        var filtered = await AutoTagCandidateFilter.ExcludeAutoTagIgnoredPathsAsync(
            db,
            [],
            CancellationToken.None);

        Assert.Empty(filtered);
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

    private static async Task<(int KeptPostId, int ExcludedPostId, int NonImagePostId)> SeedPostsAsync(DamebooruDbContext db)
    {
        var library = new Library
        {
            Name = "Library",
            Path = "L:\\library"
        };

        var keptPost = CreatePost(library, "allowed/image.png", "image/png", "hash-allowed");
        var excludedPost = CreatePost(library, "blocked/image.png", "image/png", "hash-blocked");
        var nonImagePost = CreatePost(library, "blocked/video.mp4", "video/mp4", "hash-video");

        db.Posts.AddRange(keptPost, excludedPost, nonImagePost);
        db.LibraryAutoTagExcludedPaths.Add(new LibraryAutoTagExcludedPath
        {
            Library = library,
            RelativePathPrefix = "blocked"
        });

        await db.SaveChangesAsync();
        return (keptPost.Id, excludedPost.Id, nonImagePost.Id);
    }

    private static Post CreatePost(Library library, string relativePath, string contentType, string hash)
        => new()
        {
            ImportDate = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
            PostFiles =
            {
                new PostFile
                {
                    Library = library,
                    RelativePath = relativePath,
                    ContentHash = hash,
                    SizeBytes = 100,
                    Width = 10,
                    Height = 10,
                    ContentType = contentType,
                    FileModifiedDate = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
                }
            }
        };
}
