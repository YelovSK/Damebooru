using System.Net;
using System.Text;
using Damebooru.Core.Config;
using Damebooru.Core.Entities;
using Damebooru.Core.External;
using Damebooru.Processing.Infrastructure.External.SauceNao;
using Microsoft.Extensions.Logging.Abstractions;

namespace Damebooru.Tests;

public sealed class SauceNaoClientTests
{
    [Fact]
    public async Task SearchAsync_429WithShortLimitHeader_DoesNotStopRun()
    {
        var client = CreateClient(TooManyRequestsResponse("""
            {"header":{"status":-2,"message":"<strong>Search Rate Too High.</strong><br />Your IP has exceeded the basic account type's rate limit of 4 searches every 30 seconds."}}
            """));

        var exception = await SearchAndCaptureException(client);

        Assert.Contains("Search Rate Too High", exception.Message);
        Assert.True(exception.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        Assert.False(exception.StopCurrentRun);
    }

    [Fact]
    public async Task SearchAsync_429WithNonJsonBody_TreatsAsShortTermRateLimit()
    {
        var client = CreateClient(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                "<strong>Search Rate Too High.</strong><br />Your IP has exceeded the rate limit.",
                Encoding.UTF8,
                "text/html"),
        });

        var exception = await SearchAndCaptureException(client);

        Assert.True(exception.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        Assert.False(exception.StopCurrentRun);
    }

    [Fact]
    public async Task SearchAsync_429WithDailyLimitHeader_StopsRun()
    {
        var client = CreateClient(TooManyRequestsResponse("""
            {"header":{"status":-2,"message":"Daily Search Limit Exceeded. Please wait for your limit to reset."}}
            """));

        var exception = await SearchAndCaptureException(client);

        Assert.True(exception.IsRetryable);
        Assert.True(exception.StopCurrentRun);
    }

    private static SauceNaoClient CreateClient(HttpResponseMessage response)
        => new(
            new HttpClient(new StubHttpMessageHandler(response))
            {
                BaseAddress = new Uri("https://saucenao.test"),
            },
            new DamebooruConfig
            {
                ExternalApis = { SauceNao = { ApiKey = "test-api-key" } },
            },
            new SauceNaoRateCoordinator(),
            NullLogger<SauceNaoClient>.Instance);

    private static HttpResponseMessage TooManyRequestsResponse(string json)
        => new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static async Task<ExternalProviderException> SearchAndCaptureException(SauceNaoClient client)
    {
        await using var image = new MemoryStream([0x00]);
        return await Assert.ThrowsAsync<ExternalProviderException>(
            () => client.SearchAsync(image, "image.png", "image/png"));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
