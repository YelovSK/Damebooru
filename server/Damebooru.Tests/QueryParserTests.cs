using Damebooru.Processing.Pipeline;

namespace Damebooru.Tests;

public class QueryParserTests
{
    [Fact]
    public void Parse_EmptyQuery_ReturnsDefaults()
    {
        var parsed = QueryParser.Parse("   ");

        Assert.Empty(parsed.IncludedTags);
        Assert.Empty(parsed.ExcludedTags);
        Assert.Equal(SearchSortField.FileModifiedDate, parsed.SortField);
        Assert.Equal(SearchSortDirection.Desc, parsed.SortDirection);
        Assert.Null(parsed.TagCountFilter);
        Assert.Null(parsed.FavoriteFilter);
    }

    [Fact]
    public void Parse_TagsAndNegatedTags_MapsCorrectly()
    {
        var parsed = QueryParser.Parse("cat -dog artist\\:foo");

        Assert.Equal(["cat", "artist:foo"], parsed.IncludedTags);
        Assert.Equal(["dog"], parsed.ExcludedTags);
    }

    [Fact]
    public void Parse_TypeDirective_MapsIncludedAndExcludedMediaTypes()
    {
        var parsed = QueryParser.Parse("type:image,mp4 -type:gif");

        Assert.Contains(PostMediaType.Image, parsed.IncludedMediaTypes);
        Assert.Contains(PostMediaType.Video, parsed.IncludedMediaTypes);
        Assert.Contains(PostMediaType.Animation, parsed.ExcludedMediaTypes);
    }

    [Fact]
    public void Parse_TagCountDirective_ParsesOperatorAndValue()
    {
        var parsed = QueryParser.Parse("tag-count:>=12");

        Assert.NotNull(parsed.TagCountFilter);
        Assert.Equal(NumericComparisonOperator.GreaterThanOrEqual, parsed.TagCountFilter!.Operator);
        Assert.Equal(12, parsed.TagCountFilter.Value);
    }

    [Fact]
    public void Parse_InvalidTagCountDirective_DoesNotFallbackToTag()
    {
        var parsed = QueryParser.Parse("tag-count:abc");

        Assert.Null(parsed.TagCountFilter);
        Assert.Empty(parsed.IncludedTags);
    }

    [Fact]
    public void Parse_FavoriteDirective_HandlesNegation()
    {
        var parsed = QueryParser.Parse("-favorite:true");

        Assert.False(parsed.FavoriteFilter);
    }

    [Theory]
    [InlineData("sort:new", SearchSortField.FileModifiedDate, SearchSortDirection.Desc)]
    [InlineData("sort:old", SearchSortField.FileModifiedDate, SearchSortDirection.Asc)]
    [InlineData("sort:id:desc", SearchSortField.Id, SearchSortDirection.Desc)]
    [InlineData("sort:+id", SearchSortField.Id, SearchSortDirection.Asc)]
    [InlineData("sort:tag-count", SearchSortField.TagCount, SearchSortDirection.Asc)]
    [InlineData("sort:size_desc", SearchSortField.SizeBytes, SearchSortDirection.Desc)]
    public void Parse_SortDirective_ParsesSupportedSyntax(
        string query,
        SearchSortField expectedField,
        SearchSortDirection expectedDirection)
    {
        var parsed = QueryParser.Parse(query);

        Assert.Equal(expectedField, parsed.SortField);
        Assert.Equal(expectedDirection, parsed.SortDirection);
    }

    [Fact]
    public void Parse_FilenameDirective_SupportsMultipleValues()
    {
        var parsed = QueryParser.Parse("file:cat.png,dog.mp4 -filename:skip.webp");

        Assert.Equal(["cat.png", "dog.mp4"], parsed.IncludedFilenames);
        Assert.Equal(["skip.webp"], parsed.ExcludedFilenames);
    }
}
