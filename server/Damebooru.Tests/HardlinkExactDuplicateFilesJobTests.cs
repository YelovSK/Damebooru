using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Jobs;
using Damebooru.Processing.Scanning;
using Damebooru.Processing.Services.Files;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public class HardlinkExactDuplicateFilesJobTests
{
    [Fact]
    public async Task ExecuteAsync_HardlinksExactDuplicateFilesOnSameDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bakabooru-hardlink-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<DamebooruDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IHardLinkService, PlatformHardLinkService>();
            services.AddSingleton<IFileIdentityResolver>(_ => new PlatformFileIdentityResolver(NullLogger<PlatformFileIdentityResolver>.Instance));

            var fileA = Path.Combine(root, "a.bin");
            var fileB = Path.Combine(root, "b.bin");
            await File.WriteAllBytesAsync(fileA, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(fileB, [1, 2, 3, 4]);

            await using (var setupScope = services.BuildServiceProvider().CreateAsyncScope())
            {
                var setupDb = setupScope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
                var identityResolver = setupScope.ServiceProvider.GetRequiredService<IFileIdentityResolver>();
                await setupDb.Database.EnsureCreatedAsync();

                var library = new Library { Name = "Library", Path = root };
                setupDb.Libraries.Add(library);
                await setupDb.SaveChangesAsync();

                var identityA = identityResolver.TryResolve(fileA)!;
                var identityB = identityResolver.TryResolve(fileB)!;
                setupDb.Posts.AddRange(
                    new Post
                    {
                        ImportDate = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
                        PostFiles =
                        [
                            new PostFile
                            {
                                LibraryId = library.Id,
                                RelativePath = "a.bin",
                                ContentHash = "same-hash",
                                ContentType = "application/octet-stream",
                                SizeBytes = 4,
                                FileModifiedDate = File.GetLastWriteTimeUtc(fileA),
                                FileIdentityDevice = identityA.Device,
                                FileIdentityValue = identityA.Value,
                            }
                        ]
                    },
                    new Post
                    {
                        ImportDate = new DateTime(2026, 4, 1, 11, 0, 0, DateTimeKind.Utc),
                        PostFiles =
                        [
                            new PostFile
                            {
                                LibraryId = library.Id,
                                RelativePath = "b.bin",
                                ContentHash = "same-hash",
                                ContentType = "application/octet-stream",
                                SizeBytes = 4,
                                FileModifiedDate = File.GetLastWriteTimeUtc(fileB),
                                FileIdentityDevice = identityB.Device,
                                FileIdentityValue = identityB.Value,
                            }
                        ]
                    });
                await setupDb.SaveChangesAsync();
            }

            await using (var runProvider = services.BuildServiceProvider())
            {
                var job = new HardlinkExactDuplicateFilesJob(
                    runProvider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<HardlinkExactDuplicateFilesJob>.Instance,
                    runProvider.GetRequiredService<IHardLinkService>(),
                    runProvider.GetRequiredService<IFileIdentityResolver>());

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
                var files = await db.PostFiles.OrderBy(pf => pf.Id).ToListAsync();
                Assert.Equal(2, files.Count);
                Assert.Equal(files[0].FileIdentityDevice, files[1].FileIdentityDevice);
                Assert.Equal(files[0].FileIdentityValue, files[1].FileIdentityValue);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
