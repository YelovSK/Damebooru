using System.Security.Claims;
using Bakabooru.Core.Config;
using Bakabooru.Server.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IOptions<BakabooruConfig> _config;

    public AuthController(IOptions<BakabooruConfig> config)
    {
        _config = config;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<AuthSessionDto>> Login([FromBody] LoginRequestDto request)
    {
        var auth = _config.Value.Auth;

        if (!auth.Enabled)
        {
            return BadRequest("Authentication is disabled.");
        }

        if (!string.Equals(request.Username, auth.Username, StringComparison.Ordinal)
            || !string.Equals(request.Password, auth.Password, StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, auth.Username),
            new(ClaimTypes.Name, auth.Username)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            });

        return Ok(new AuthSessionDto
        {
            Username = auth.Username,
            IsAuthenticated = true
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("me")]
    public ActionResult<AuthSessionDto> Me()
    {
        var isAuthenticated = User.Identity?.IsAuthenticated ?? false;
        if (!isAuthenticated)
        {
            return Unauthorized();
        }

        return Ok(new AuthSessionDto
        {
            Username = User.Identity?.Name ?? "user",
            IsAuthenticated = true
        });
    }
}
