using Damebooru.Processing.Services.Duplicates;

namespace Damebooru.Tests;

public class DuplicatePathHelperTests
{
    [Theory]
    [InlineData("file.jpg", "")]
    [InlineData("folder/file.jpg", "folder")]
    [InlineData("folder/sub/file.jpg", "folder/sub")]
    [InlineData("folder\\sub\\file.jpg", "folder/sub")]
    public void GetParentFolderPath_ReturnsExpectedParent(string relativePath, string expected)
    {
        var parent = DuplicatePathHelper.GetParentFolderPath(relativePath);

        Assert.Equal(expected, parent);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(".", "")]
    [InlineData("/folder/sub/", "folder/sub")]
    [InlineData("folder\\sub", "folder/sub")]
    public void NormalizeFolderPath_NormalizesAsExpected(string input, string expected)
    {
        var normalized = DuplicatePathHelper.NormalizeFolderPath(input);

        Assert.Equal(expected, normalized);
    }
}
