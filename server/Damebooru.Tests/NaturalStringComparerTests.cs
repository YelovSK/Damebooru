using Damebooru.Processing.Services;

namespace Damebooru.Tests;

public sealed class NaturalStringComparerTests
{
    [Fact]
    public void Sort_ComparesNumbersByValue()
    {
        string[] names = ["Page 10.png", "page 2.png", "page 09.png", "page 1.png", "cover.png", "page 100.png"];

        var sorted = names.Order(NaturalStringComparer.Instance).ToArray();

        Assert.Equal(["cover.png", "page 1.png", "page 2.png", "page 09.png", "Page 10.png", "page 100.png"], sorted);
    }
}
