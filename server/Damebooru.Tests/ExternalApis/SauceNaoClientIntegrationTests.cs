using Damebooru.Core.Interfaces;

namespace Damebooru.Tests;

public sealed class SauceNaoClientIntegrationTests
{
    private const string SampleImageUrl = "https://cdn.donmai.us/original/80/ed/__frieren_sousou_no_frieren_drawn_by_khyle__80ed1d55cff5989b83ddf24409b15933.jpg";

    [Fact]
    public async Task SearchAsync_returns_matches_from_live_api()
    {
        var settings = ExternalApiTestSettings.Load();
        Assert.False(string.IsNullOrWhiteSpace(settings.Config.ExternalApis.SauceNao.ApiKey), "Configure Damebooru:ExternalApis:SauceNao:ApiKey before running SauceNAO integration tests.");

        var client = ExternalApiTestClientFactory.CreateClient<ISauceNaoClient>(settings.Config);

        await using var stream = await OpenSauceNaoSampleAsync();
        var result = await client.SearchAsync(stream, "integration-sample.jpg", "image/jpeg");

        Assert.True(result.Status >= 0);
        Assert.NotNull(result.Matches);
        Assert.NotEmpty(result.Matches);
        Assert.Contains(result.Matches, match => match.ExternalUrls.Count > 0 || match.DanbooruPostId.HasValue || match.GelbooruPostId.HasValue || match.PixivPostId.HasValue);
    }

    private static async Task<Stream> OpenSauceNaoSampleAsync()
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Damebooru-Test/1.0");
        var bytes = await httpClient.GetByteArrayAsync(SampleImageUrl);
        return new MemoryStream(bytes, writable: false);
    }
}
