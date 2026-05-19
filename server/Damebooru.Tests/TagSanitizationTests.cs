using Damebooru.Core.Entities;

namespace Damebooru.Tests;

public class TagSanitizationTests
{
    [Theory]
    [InlineData("  Artist:John_Doe  ", "artist_john_doe")]
    [InlineData("A::B", "a_b")]
    [InlineData("___tag___", "tag")]
    [InlineData("MIXED_Case", "mixed_case")]
    [InlineData("name::::value", "name_value")]
    [InlineData(" : : ", " ")]
    public void NormalizeName_ReturnsCanonicalForm(string input, string expected)
    {
        var sanitized = Tag.NormalizeName(input);

        Assert.Equal(expected, sanitized);
    }
}
