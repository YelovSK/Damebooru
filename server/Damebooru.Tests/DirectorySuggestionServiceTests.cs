using Damebooru.Processing.Services.Files;

namespace Damebooru.Tests;

public sealed class DirectorySuggestionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "damebooru-directory-tests", Guid.NewGuid().ToString("N"));
    private readonly DirectorySuggestionService _service = new();

    public DirectorySuggestionServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Media"));
        Directory.CreateDirectory(Path.Combine(_root, "memes"));
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        File.WriteAllText(Path.Combine(_root, "menu.txt"), "");
    }

    [Fact]
    public void Suggest_CompletesPartialNameCaseInsensitively()
    {
        var suggestions = _service.Suggest(Path.Combine(_root, "me"));

        Assert.Equal([Dir("Media"), Dir("memes")], suggestions);
    }

    [Fact]
    public void Suggest_ListsChildrenAfterTrailingSeparator()
    {
        var suggestions = _service.Suggest(_root + Path.DirectorySeparatorChar);

        Assert.Equal([Dir("Media"), Dir("memes"), Dir("photos")], suggestions);
    }

    [Fact]
    public void Suggest_ReturnsNothingForMissingDirectory()
    {
        Assert.Empty(_service.Suggest(Path.Combine(_root, "missing", "x")));
    }

    [Fact]
    public void Suggest_OffersStartingPointsWhenEmpty()
    {
        Assert.NotEmpty(_service.Suggest(""));
    }

    private string Dir(string name) => Path.Combine(_root, name) + Path.DirectorySeparatorChar;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
