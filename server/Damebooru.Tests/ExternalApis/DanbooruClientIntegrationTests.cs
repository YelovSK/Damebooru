using Damebooru.Core.Interfaces;
using Damebooru.Core.Entities;

namespace Damebooru.Tests;

public sealed class DanbooruClientIntegrationTests
{
    private const long SamplePostId = 10342182;

    [Fact]
    public async Task GetPostDetailsAsync_returns_live_post_details()
    {
        var settings = ExternalApiTestSettings.Load();
        var client = ExternalApiTestClientFactory.CreateClient<IDanbooruClient>(settings.Config);
        var result = await client.GetPostDetailsAsync(SamplePostId);

        Assert.NotNull(result);
        Assert.Equal(AutoTagProvider.Danbooru, result!.Provider);
        Assert.Equal(SamplePostId, result.PostId);
        Assert.StartsWith("https://danbooru.donmai.us/posts/", result.CanonicalUrl, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Tags);
    }
}
