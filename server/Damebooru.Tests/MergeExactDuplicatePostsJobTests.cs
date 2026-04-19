using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public class MergeExactDuplicatePostsJobTests
{
    [Fact]
    public async Task ExecuteAsync_MergesExistingExactHashSiblingPosts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<DamebooruDbContext>(options => options.UseSqlite(connection));

        await using (var setupScope = services.BuildServiceProvider().CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
            await setupDb.Database.EnsureCreatedAsync();

            var library = new Library { Name = "Library", Path = "C:/Library" };
            var tagA = new Tag { Name = "tag-a", Category = TagCategoryKind.General };
            var tagB = new Tag { Name = "tag-b", Category = TagCategoryKind.General };
            setupDb.Libraries.Add(library);
            setupDb.Tags.AddRange(tagA, tagB);
            await setupDb.SaveChangesAsync();

            var olderPost = new Post
            {
                ImportDate = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
                IsFavorite = false,
                PostFiles =
                [
                    new PostFile
                    {
                        LibraryId = library.Id,
                        RelativePath = "folder-a/image.png",
                        ContentHash = "same-hash",
                        SizeBytes = 100,
                        Width = 100,
                        Height = 100,
                        ContentType = "image/png",
                        FileModifiedDate = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
                    }
                ],
                Sources =
                [
                    new PostSource { Url = "https://source-a", Order = 0 }
                ]
            };

            var newerPost = new Post
            {
                ImportDate = new DateTime(2026, 4, 1, 11, 0, 0, DateTimeKind.Utc),
                IsFavorite = true,
                PostFiles =
                [
                    new PostFile
                    {
                        LibraryId = library.Id,
                        RelativePath = "folder-b/image.png",
                        ContentHash = "same-hash",
                        SizeBytes = 100,
                        Width = 100,
                        Height = 100,
                        ContentType = "image/png",
                        FileModifiedDate = new DateTime(2026, 4, 1, 11, 0, 0, DateTimeKind.Utc),
                    }
                ],
                Sources =
                [
                    new PostSource { Url = "https://source-b", Order = 0 }
                ]
            };

            setupDb.Posts.AddRange(olderPost, newerPost);
            await setupDb.SaveChangesAsync();

            setupDb.PostTags.AddRange(
                new PostTag { PostId = olderPost.Id, TagId = tagA.Id, Source = PostTagSource.Manual },
                new PostTag { PostId = newerPost.Id, TagId = tagB.Id, Source = PostTagSource.Manual });
            await setupDb.SaveChangesAsync();
        }

        await using (var runProvider = services.BuildServiceProvider())
        {
            var job = new MergeExactDuplicatePostsJob(
                runProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MergeExactDuplicatePostsJob>.Instance);

            await job.ExecuteAsync(new JobContext
            {
                JobId = Guid.NewGuid().ToString(),
                CancellationToken = CancellationToken.None,
                Reporter = NullJobReporter.Instance,
            });
        }

        await using (var assertScope = services.BuildServiceProvider().CreateAsyncScope())
        {
            var db = assertScope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
            var posts = await db.Posts
                .Include(p => p.PostFiles)
                .Include(p => p.Sources)
                .Include(p => p.PostTags)
                .OrderBy(p => p.Id)
                .ToListAsync();

            var post = Assert.Single(posts);
            Assert.True(post.IsFavorite);
            Assert.Equal(2, post.PostFiles.Count);
            Assert.Equal(2, post.Sources.Count);
            Assert.Equal(2, post.PostTags.Count);
            Assert.Contains(post.PostFiles, pf => pf.RelativePath == "folder-a/image.png");
            Assert.Contains(post.PostFiles, pf => pf.RelativePath == "folder-b/image.png");
            Assert.Contains(post.Sources, s => s.Url == "https://source-a");
            Assert.Contains(post.Sources, s => s.Url == "https://source-b");
        }
    }
}
