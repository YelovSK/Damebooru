using Damebooru.Processing.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public sealed class GeneratedImageLayoutMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "damebooru-layout-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Run_MovesPerLibraryImagesToHashOnlyLayout()
    {
        var previews = Path.Combine(_root, "previews");
        var thumbnails = Path.Combine(_root, "thumbnails");
        Write(Path.Combine(previews, "1", "aaa.webp"), "library 1");
        Write(Path.Combine(previews, "2", "aaa.webp"), "library 2");
        Write(Path.Combine(previews, "2", "bbb.webp"), "b");
        Write(Path.Combine(thumbnails, "200", "1", "aaa.webp"), "thumb");

        GeneratedImageLayoutMigration.Run(previews, thumbnails, NullLogger.Instance);

        Assert.Equal(["aaa.webp", "bbb.webp"], Directory.GetFiles(previews).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(previews));
        Assert.Equal(["aaa.webp"], Directory.GetFiles(Path.Combine(thumbnails, "200")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(Path.Combine(thumbnails, "200")));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
