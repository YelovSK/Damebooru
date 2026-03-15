using Damebooru.Core.Interfaces;
using Damebooru.Core.Entities;

namespace Damebooru.Tests;

public sealed class GelbooruClientIntegrationTests
{
    private const long SamplePostId = 13391919;

    [Fact]
    public async Task GetPostDetailsAsync_returns_live_post_details()
    {
        var settings = ExternalApiTestSettings.Load();
        Assert.False(string.IsNullOrWhiteSpace(settings.Config.ExternalApis.Gelbooru.ApiKey), "Configure Damebooru:ExternalApis:Gelbooru:ApiKey before running Gelbooru integration tests.");
        Assert.False(string.IsNullOrWhiteSpace(settings.Config.ExternalApis.Gelbooru.UserId), "Configure Damebooru:ExternalApis:Gelbooru:UserId before running Gelbooru integration tests.");
        var client = ExternalApiTestClientFactory.CreateClient<IGelbooruClient>(settings.Config);

        var result = await client.GetPostDetailsAsync(SamplePostId);

        Assert.NotNull(result);
        Assert.Equal(AutoTagProvider.Gelbooru, result!.Provider);
        Assert.Equal(SamplePostId, result.PostId);
        Assert.StartsWith("https://gelbooru.com/index.php?page=post&s=view&id=", result.CanonicalUrl, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Tags);
    }
}
