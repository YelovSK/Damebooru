using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services.Duplicates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public class DuplicateLookupServiceTests
{
    [Fact]
    public async Task LookupAsync_ReturnsExactAcrossLibraries_AndExcludesThemFromPerceptualResults()
    {
        await using var db = await CreateContextAsync();
        await SeedLibrariesAndPostsAsync(db, includePdqCandidates: true);

        var service = new DuplicateLookupService(
            db,
            new StubHasher("exact-hash"),
            new StubSimilarityService(new SimilarityHashes(new string('f', 64))),
            NullLogger<DuplicateLookupService>.Instance);

        var result = await service.LookupAsync(
            () => new MemoryStream([1, 2, 3], writable: false),
            "upload.png",
            "image/png");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.ExactMatches.Count);
        Assert.Single(result.Value.PerceptualMatches);
        Assert.DoesNotContain(result.Value.PerceptualMatches, match => match.ContentHash == "exact-hash");
        Assert.All(result.Value.ExactMatches, match => Assert.Null(match.SimilarityPercent));
    }

    [Fact]
    public async Task LookupAsync_NonImageUpload_ReturnsReasonAndSkipsPerceptualMatching()
    {
        await using var db = await CreateContextAsync();
        await SeedLibrariesAndPostsAsync(db, includePdqCandidates: true);

        var service = new DuplicateLookupService(
            db,
            new StubHasher("exact-hash"),
            new StubSimilarityService(new SimilarityHashes(new string('f', 64))),
            NullLogger<DuplicateLookupService>.Instance);

        var result = await service.LookupAsync(
            () => new MemoryStream([9, 9, 9], writable: false),
            "clip.mp4",
            "video/mp4");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Perceptual matching is only available for image uploads.", result.Value!.PerceptualUnavailableReason);
        Assert.False(result.Value.PerceptualHashComputed);
        Assert.Empty(result.Value.PerceptualMatches);
    }

    [Fact]
    public async Task LookupAsync_NoStoredPdqHashes_ReturnsGuidanceMessage()
    {
        await using var db = await CreateContextAsync();
        await SeedLibrariesAndPostsAsync(db, includePdqCandidates: false);

        var service = new DuplicateLookupService(
            db,
            new StubHasher("exact-hash"),
            new StubSimilarityService(new SimilarityHashes(new string('f', 64))),
            NullLogger<DuplicateLookupService>.Instance);

        var result = await service.LookupAsync(
            () => new MemoryStream([4, 5, 6], writable: false),
            "upload.png",
            "image/png");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("No stored perceptual hashes are available. Run the Compute Similarity job first.", result.Value!.PerceptualUnavailableReason);
        Assert.Empty(result.Value.PerceptualMatches);
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

    private static async Task SeedLibrariesAndPostsAsync(DamebooruDbContext context, bool includePdqCandidates)
    {
        var libraryA = new Library { Name = "Alpha", Path = "A" };
        var libraryB = new Library { Name = "Beta", Path = "B" };
        context.Libraries.AddRange(libraryA, libraryB);
        await context.SaveChangesAsync();

        var now = new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc);
        context.Posts.AddRange(
            new Post
            {
                ImportDate = now,
                PostFiles =
                [
                    new PostFile
                    {
                        LibraryId = libraryA.Id,
                        RelativePath = "exact/a.png",
                        ContentHash = "exact-hash",
                        PdqHash256 = includePdqCandidates ? new string('0', 64) : null,
                        SizeBytes = 100,
                        Width = 100,
                        Height = 100,
                        ContentType = "image/png",
                        FileModifiedDate = now,
                    }
                ],
            },
            new Post
            {
                ImportDate = now.AddMinutes(1),
                PostFiles =
                [
                    new PostFile
                    {
                        LibraryId = libraryB.Id,
                        RelativePath = "exact/b.png",
                        ContentHash = "exact-hash",
                        PdqHash256 = includePdqCandidates ? new string('1', 64) : null,
                        SizeBytes = 120,
                        Width = 110,
                        Height = 110,
                        ContentType = "image/png",
                        FileModifiedDate = now.AddMinutes(1),
                    }
                ],
            },
            new Post
            {
                ImportDate = now.AddMinutes(2),
                PostFiles =
                [
                    new PostFile
                    {
                        LibraryId = libraryA.Id,
                        RelativePath = "similar/c.png",
                        ContentHash = "different-hash",
                        PdqHash256 = includePdqCandidates ? new string('f', 64) : null,
                        SizeBytes = 140,
                        Width = 120,
                        Height = 120,
                        ContentType = "image/png",
                        FileModifiedDate = now.AddMinutes(2),
                    }
                ],
            });

        await context.SaveChangesAsync();
    }

    private sealed class StubHasher : IHasherService
    {
        private readonly string _hash;

        public StubHasher(string hash)
        {
            _hash = hash;
        }

        public Task<string> ComputeContentHashAsync(Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(_hash);

        public Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(_hash);
    }

    private sealed class StubSimilarityService : ISimilarityService
    {
        private readonly SimilarityHashes _hashes;

        public StubSimilarityService(SimilarityHashes hashes)
        {
            _hashes = hashes;
        }

        public Task<SimilarityHashes> ComputeHashesAsync(Stream stream, CancellationToken cancellationToken = default)
            => Task.FromResult(_hashes);

        public Task<SimilarityHashes> ComputeHashesAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(_hashes);
    }
}
