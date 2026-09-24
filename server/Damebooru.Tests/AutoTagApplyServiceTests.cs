using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services.AutoTagging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class AutoTagApplyServiceTests
{
    [Fact]
    public async Task ApplyScanAsync_KeepsProviderTagsWhenMetadataStepFailed()
    {
        await using var db = await CreateContextAsync();
        var library = new Library { Name = "Library", Path = Path.GetTempPath() };
        var tag = new Tag { Name = "kept_tag", Category = TagCategoryKind.General };
        db.Libraries.Add(library);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var post = new Post
        {
            ImportDate = DateTime.UtcNow,
            ContentHash = "hash",
            ContentType = "image/png",
            PostFiles = [new PostFile { LibraryId = library.Id, RelativePath = "a.png" }],
            PostTags = [new PostTag { TagId = tag.Id, Source = PostTagSource.Danbooru }],
        };
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        // Discovery was skipped because an earlier provider matched; fetching the Danbooru post then failed.
        db.PostAutoTagScans.Add(new PostAutoTagScan
        {
            PostId = post.Id,
            ContentHash = "hash",
            Status = AutoTagScanStatus.Partial,
            Steps =
            [
                new PostAutoTagScanStep { Provider = AutoTagProvider.Danbooru, Kind = AutoTagScanStepKind.Discovery, Status = AutoTagScanStepStatus.Skipped },
                new PostAutoTagScanStep { Provider = AutoTagProvider.Danbooru, Kind = AutoTagScanStepKind.Metadata, Status = AutoTagScanStepStatus.RetryableFailure },
            ],
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new AutoTagApplyService(db).ApplyScanAsync(post.Id);

        Assert.Equal(0, result.RemovedTags);
        Assert.True(await db.PostTags.AnyAsync(pt => pt.PostId == post.Id && pt.TagId == tag.Id));
    }

    private static async Task<DamebooruDbContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new DamebooruDbContext(new DbContextOptionsBuilder<DamebooruDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();
        return context;
    }
}
