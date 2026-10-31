using Damebooru.Core.Entities;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Tests;

public sealed class FolderSuggestionServiceTests
{
    [Fact]
    public async Task Suggest_FolderNamedAfterCharacter_WinsAndReplacesItsSeriesFolder()
    {
        await using var s = await Scenario.CreateAsync();
        s.AddSorted("Genshin Impact/Raiden Shogun/a.png", ("1girl", TagCategoryKind.General));
        s.AddSorted("Genshin Impact/b.png", ("1girl", TagCategoryKind.General));
        s.AddSorted("Other/c.png", ("1girl", TagCategoryKind.General));
        var inboxPost = s.AddInbox(("1girl", TagCategoryKind.General));
        s.Predict(inboxPost, "raiden_shogun", TagCategoryKind.Character, 0.9m);
        s.Predict(inboxPost, "genshin_impact", TagCategoryKind.Copyright, 0.95m);
        await s.SaveAsync();

        var suggestions = await s.SuggestAsync(inboxPost);

        var top = suggestions[0];
        Assert.Equal("Genshin Impact/Raiden Shogun", top.Folder);
        Assert.True(top.Score > 0.85);
        Assert.DoesNotContain(suggestions, x => x.Folder == "Genshin Impact");
    }

    [Fact]
    public async Task Suggest_GeneralTagsNeverPointAnywhere()
    {
        await using var s = await Scenario.CreateAsync();
        for (var i = 0; i < 4; i++)
        {
            s.AddSorted($"wallpapers/{i}.png", ("scenery", TagCategoryKind.General));
        }

        var inboxPost = s.AddInbox(("scenery", TagCategoryKind.General));
        await s.SaveAsync();

        Assert.Empty(await s.SuggestAsync(inboxPost));
    }

    [Fact]
    public async Task Suggest_FlagsImagesTheTaggerHasNotSeen()
    {
        await using var s = await Scenario.CreateAsync();
        var unseen = s.AddInbox();
        var seen = s.AddInbox();
        seen.AiPredictionDate = DateTime.UtcNow;
        var video = s.AddInbox();
        video.ContentType = "video/mp4";
        await s.SaveAsync();

        var results = await s.Service.SuggestAsync([unseen.Id, seen.Id, video.Id], CancellationToken.None);

        Assert.Equal([true, false, false], results.Select(r => r.NeedsPrediction));
    }

    [Fact]
    public async Task Suggest_IgnoresFolderTags()
    {
        await using var s = await Scenario.CreateAsync();
        s.AddSorted("rem/a.png", ("rem", TagCategoryKind.Character));
        var inboxPost = s.AddInbox();
        s.Tag(inboxPost, "rem", TagCategoryKind.Character, PostTagSource.Folder);
        await s.SaveAsync();

        Assert.Empty(await s.SuggestAsync(inboxPost));
    }

    [Fact]
    public async Task Evaluate_IgnoresFoldersOnlyTheEvaluatedPostLivesIn()
    {
        await using var s = await Scenario.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            s.AddSorted($"rem/{i}.png", ("rem", TagCategoryKind.Character));
        }

        s.AddSorted("ram/only.png", ("ram", TagCategoryKind.Character));
        await s.SaveAsync();

        var evaluation = await s.Service.EvaluateAsync(100, CancellationToken.None);

        Assert.Equal(4, evaluation.Sampled);
        Assert.Equal(3, evaluation.Top1Hits);
        Assert.Equal(1, evaluation.WithoutSuggestions);
        Assert.Single(evaluation.Misses);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly DamebooruDbContext _db;
        private readonly Library _library = new() { Name = "Library", Path = "L:\\library" };
        private readonly Library _inbox = new() { Name = "Inbox", Path = "L:\\inbox", Kind = LibraryKind.Inbox };
        private readonly Dictionary<string, Tag> _tags = [];
        private int _hash;

        private Scenario(DamebooruDbContext db)
        {
            _db = db;
            Service = new FolderSuggestionService(db);
        }

        public FolderSuggestionService Service { get; }

        public static async Task<Scenario> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new DamebooruDbContext(new DbContextOptionsBuilder<DamebooruDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Scenario(db);
        }

        public Post AddSorted(string relativePath, params (string Name, TagCategoryKind Category)[] tags)
            => AddPost(_library, relativePath, tags);

        public Post AddInbox(params (string Name, TagCategoryKind Category)[] tags)
            => AddPost(_inbox, $"{_hash}.png", tags);

        public void Tag(Post post, string name, TagCategoryKind category, PostTagSource source)
        {
            var tag = _tags.TryGetValue(name, out var existing) ? existing : _tags[name] = new Tag { Name = name, Category = category };
            tag.PostCount++;
            post.PostTags.Add(new PostTag { Tag = tag, Source = source });
        }

        public void Predict(Post post, string name, TagCategoryKind category, decimal score)
            => _db.PostAiPredictions.Add(new PostAiPrediction { Post = post, TagName = name, Category = category, Score = score });

        public Task SaveAsync() => _db.SaveChangesAsync();

        public async Task<List<Core.DTOs.FolderSuggestionDto>> SuggestAsync(Post post)
            => (await Service.SuggestAsync([post.Id], CancellationToken.None)).Single().Suggestions;

        private Post AddPost(Library library, string relativePath, (string Name, TagCategoryKind Category)[] tags)
        {
            var post = new Post
            {
                ImportDate = DateTime.UtcNow,
                ContentHash = $"hash-{_hash++}",
                ContentType = "image/png",
                PostFiles = [new PostFile { Library = library, RelativePath = relativePath }],
            };
            foreach (var (name, category) in tags)
            {
                Tag(post, name, category, PostTagSource.Manual);
            }

            _db.Posts.Add(post);
            return post;
        }

        public ValueTask DisposeAsync() => _db.DisposeAsync();
    }
}
