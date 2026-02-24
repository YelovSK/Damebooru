using Damebooru.Core.Paths;

namespace Damebooru.Tests;

public class RelativePathMatcherTests
{
    [Theory]
    [InlineData("folder\\sub//leaf/", "folder/sub/leaf")]
    [InlineData("  /a/b/c  ", "a/b/c")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizePath_NormalizesAsExpected(string input, string expected)
    {
        var normalized = RelativePathMatcher.NormalizePath(input);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("cats", "cats", true)]
    [InlineData("cats/kitten.jpg", "cats", true)]
    [InlineData("cats-and-dogs/file.jpg", "cats", false)]
    [InlineData("", "cats", false)]
    [InlineData("cats/kitten.jpg", "", false)]
    public void IsWithinPrefix_MatchesFolderBoundaries(string path, string prefix, bool expected)
    {
        var result = RelativePathMatcher.IsWithinPrefix(path, prefix);

        Assert.Equal(expected, result);
    }
}
