using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Damebooru.Core.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Damebooru.Server.Infrastructure;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<DamebooruConfig> config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKey = config.Value.Auth.ApiKey;
        if (apiKey.Length == 0 || !Request.Headers.TryGetValue("X-Api-Key", out var provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided.ToString()),
                Encoding.UTF8.GetBytes(apiKey)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
