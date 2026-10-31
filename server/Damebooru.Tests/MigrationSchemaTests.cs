using Damebooru.Core.Entities;
using Damebooru.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

/// <summary>
/// The triggers are raw SQL that EF doesn't know about, and SQLite rebuilds a table to alter a column, so a later
/// migration can silently drop or break them. These run the real migrations and check the triggers survived.
/// </summary>
public sealed class MigrationSchemaTests : IAsyncLifetime
{
    private static readonly string[] ExpectedTriggers =
    [
        "trg_postfiles_ad_post_file_modified_date",
        "trg_postfiles_ai_post_file_modified_date",
        "trg_postfiles_au_post_file_modified_date",
        "trg_posts_au_audit_is_favorite",
        "trg_postsources_ad_audit",
        "trg_postsources_ai_audit",
        "trg_postsources_au_audit_url",
        "trg_posttags_ad_audit",
        "trg_posttags_ad_update_tag_postcount",
        "trg_posttags_ai_audit",
        "trg_posttags_ai_update_tag_postcount",
        "trg_posttags_au_update_tag_postcount",
    ];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DamebooruDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new DamebooruDbContext(new DbContextOptionsBuilder<DamebooruDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Migrations_CreateEveryTrigger()
    {
        var triggers = await _db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' ORDER BY name")
            .ToListAsync();

        Assert.Equal(ExpectedTriggers.Order(StringComparer.Ordinal), triggers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PostFileTriggers_KeepPostFileModifiedDateAtEarliestFile()
    {
        var later = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        var earlier = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var post = await AddPostAsync(later);

        var earlierFile = new PostFile { PostId = post.Id, LibraryId = post.PostFiles.First().LibraryId, RelativePath = "copy.png", FileModifiedDate = earlier };
        _db.PostFiles.Add(earlierFile);
        await _db.SaveChangesAsync();
        Assert.Equal(earlier, await GetFileModifiedDateAsync(post.Id));

        _db.PostFiles.Remove(earlierFile);
        await _db.SaveChangesAsync();
        Assert.Equal(later, await GetFileModifiedDateAsync(post.Id));
    }

    [Fact]
    public async Task FavoriteTrigger_WritesAuditEntry()
    {
        var post = await AddPostAsync(DateTime.UtcNow);

        post.IsFavorite = true;
        await _db.SaveChangesAsync();

        Assert.Equal(1, await _db.PostAuditEntries.CountAsync(e => e.PostId == post.Id && e.Field == "IsFavorite"));
    }

    private async Task<Post> AddPostAsync(DateTime fileModifiedDate)
    {
        var library = new Library { Name = "Library", Path = "/library" };
        _db.Libraries.Add(library);
        await _db.SaveChangesAsync();

        var post = new Post
        {
            ImportDate = DateTime.UtcNow,
            ContentHash = Guid.NewGuid().ToString("N"),
            ContentType = "image/png",
            PostFiles = [new PostFile { LibraryId = library.Id, RelativePath = "a.png", FileModifiedDate = fileModifiedDate }],
        };
        _db.Posts.Add(post);
        await _db.SaveChangesAsync();
        return post;
    }

    private Task<DateTime> GetFileModifiedDateAsync(int postId)
        => _db.Posts.AsNoTracking().Where(p => p.Id == postId).Select(p => p.FileModifiedDate).SingleAsync();
}
