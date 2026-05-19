using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class PostWriteServiceTagNormalizationTests
{
    [Fact]
    public async Task AddAndRemoveTagAsync_NormalizeManualTagInput()
    {
        await using var db = await CreateContextAsync();
        var post = await SeedPostAsync(db);
        var service = new PostWriteService(db);

        var addResult = await service.AddTagAsync(post.Id, "  Artist:John_Doe  ");

        Assert.True(addResult.IsSuccess);
        var tag = await db.Tags.SingleAsync();
        Assert.Equal("artist_john_doe", tag.Name);

        var removeResult = await service.RemoveTagAsync(post.Id, "ARTIST:JOHN_DOE");

        Assert.True(removeResult.IsSuccess);
        Assert.Empty(await db.PostTags.ToListAsync());
    }

    [Fact]
    public async Task UpdateMetadataAsync_NormalizesTagNames()
    {
        await using var db = await CreateContextAsync();
        var post = await SeedPostAsync(db);
        var service = new PostWriteService(db);

        var result = await service.UpdateMetadataAsync(
            post.Id,
            new UpdatePostMetadataDto
            {
                TagsWithSources =
                [
                    new UpdatePostTagDto { Name = "A::B", Source = PostTagSource.Manual },
                    new UpdatePostTagDto { Name = "a_b", Source = PostTagSource.Manual },
                ],
            });

        Assert.True(result.IsSuccess);
        var tag = await db.Tags.SingleAsync();
        Assert.Equal("a_b", tag.Name);
        var postTag = await db.PostTags.SingleAsync();
        Assert.Equal(tag.Id, postTag.TagId);
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

    private static async Task<Post> SeedPostAsync(DamebooruDbContext db)
    {
        var post = new Post
        {
            ImportDate = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        };

        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post;
    }
}
