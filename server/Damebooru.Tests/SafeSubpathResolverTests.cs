using Damebooru.Core.Paths;

namespace Damebooru.Tests;

public class SafeSubpathResolverTests
{
    [Fact]
    public void TryResolve_ValidRelativePath_ReturnsAbsolutePath()
    {
        var root = Path.GetTempPath();

        var ok = SafeSubpathResolver.TryResolve(root, Path.Combine("a", "b.txt"), out var fullPath);

        Assert.True(ok);
        Assert.True(Path.IsPathRooted(fullPath));
        Assert.EndsWith(Path.Combine("a", "b.txt"), fullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_PathTraversal_ReturnsFalse()
    {
        var root = Path.GetTempPath();

        var ok = SafeSubpathResolver.TryResolve(root, Path.Combine("..", "outside.txt"), out var fullPath);

        Assert.False(ok);
        Assert.Equal(string.Empty, fullPath);
    }

    [Fact]
    public void TryResolve_EmptyBasePath_ReturnsFalse()
    {
        var ok = SafeSubpathResolver.TryResolve("", "file.txt", out var fullPath);

        Assert.False(ok);
        Assert.Equal(string.Empty, fullPath);
    }
}
