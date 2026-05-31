using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.Duplicates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public class DuplicateWriteServiceTests
{
    [Fact]
    public async Task DeleteDuplicatePostAsync_DeletesPostWithPrimaryFileCache()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), $"damebooru-duplicate-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libraryPath);

        try
        {
            await using var db = await CreateContextAsync();

            var deletePath = Path.Combine(libraryPath, "delete.png");
            var keepPath = Path.Combine(libraryPath, "keep.png");
            await File.WriteAllBytesAsync(deletePath, [1, 2, 3]);
            await File.WriteAllBytesAsync(keepPath, [4, 5, 6]);

            var library = new Library { Name = "Library", Path = libraryPath };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var deletePost = CreatePost(library.Id, "delete.png", "same-hash", DateTime.UtcNow);
            var keepPost = CreatePost(library.Id, "keep.png", "same-hash", DateTime.UtcNow.AddMinutes(1));
            db.Posts.AddRange(deletePost, keepPost);
            await db.SaveChangesAsync();

            var group = new DuplicateGroup
            {
                Type = DuplicateType.Exact,
                Entries =
                [
                    new DuplicateGroupEntry { PostId = deletePost.Id },
                    new DuplicateGroupEntry { PostId = keepPost.Id },
                ],
            };
            db.DuplicateGroups.Add(group);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new DuplicateWriteService(db, new FolderTaggingService());
            var result = await service.DeleteDuplicatePostAsync(group.Id, deletePost.Id);

            Assert.True(result.IsSuccess, result.Message);
            Assert.False(File.Exists(deletePath));
            Assert.True(File.Exists(keepPath));
            Assert.False(await db.Posts.AnyAsync(p => p.Id == deletePost.Id));
            Assert.True(await db.Posts.AnyAsync(p => p.Id == keepPost.Id));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
            {
                Directory.Delete(libraryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteExactDuplicateFileAsync_DeletesLastFilePostWithPrimaryFileCache()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), $"damebooru-exact-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libraryPath);

        try
        {
            await using var db = await CreateContextAsync();

            var deletePath = Path.Combine(libraryPath, "delete.png");
            await File.WriteAllBytesAsync(deletePath, [1, 2, 3]);

            var library = new Library { Name = "Library", Path = libraryPath };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var post = CreatePost(library.Id, "delete.png", "same-hash", DateTime.UtcNow);
            db.Posts.Add(post);
            await db.SaveChangesAsync();
            var postFileId = post.PostFiles.Single().Id;
            db.ChangeTracker.Clear();

            var service = new DuplicateWriteService(db, new FolderTaggingService());
            var result = await service.DeleteExactDuplicateFileAsync(postFileId);

            Assert.True(result.IsSuccess, result.Message);
            Assert.False(File.Exists(deletePath));
            Assert.False(await db.Posts.AnyAsync(p => p.Id == post.Id));
            Assert.False(await db.PostFiles.AnyAsync(pf => pf.Id == postFileId));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
            {
                Directory.Delete(libraryPath, recursive: true);
            }
        }
    }

    private static async Task<DamebooruDbContext> CreateContextAsync()
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

    private static Post CreatePost(int libraryId, string relativePath, string contentHash, DateTime timestamp)
        => new()
        {
            ImportDate = timestamp,
            PostFiles =
            [
                new PostFile
                {
                    LibraryId = libraryId,
                    RelativePath = relativePath,
                    ContentHash = contentHash,
                    SizeBytes = 100,
                    Width = 100,
                    Height = 100,
                    ContentType = "image/png",
                    FileModifiedDate = timestamp,
                },
            ],
        };
}
