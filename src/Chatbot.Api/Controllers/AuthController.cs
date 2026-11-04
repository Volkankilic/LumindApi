using Chatbot.Api.Startup;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Chatbot.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth")]
public class AuthController(IAuthService auth, AuthOptions options) : ControllerBase
{
    /// <summary>Name of the refresh cookie (HttpOnly, SameSite=Strict, Path=/api/auth).</summary>
    public const string RefreshCookie = "lumind_refresh";

    private const string CookiePath = "/api/auth";

    /// <summary>Signs in with a participant code. Every failure (bad format, unknown, revoked) is the same 401 `invalid-code`.</summary>
    [HttpPost("code")]
    [EnableRateLimiting(ApiServices.AuthCodeLimiter)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<AuthResponse> SignIn(CodeRequest request, CancellationToken ct)
    {
        var result = await auth.SignInAsync(request.Code, HttpContext.Client(), ct);
        SetRefreshCookie(result.Refresh);
        return new AuthResponse(result.AccessToken, result.ExpiresInSeconds, result.State);
    }

    /// <summary>Rotates the refresh token (cookie) and returns a new access token. A spent token revokes the whole sign-in.</summary>
    [HttpPost("refresh")]
    [EnableRateLimiting(ApiServices.AuthRefreshLimiter)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<AuthResponse> Refresh(CancellationToken ct)
    {
        try
        {
            var result = await auth.RefreshAsync(Request.Cookies[RefreshCookie], HttpContext.Client(), ct);
            SetRefreshCookie(result.Refresh);
            return new AuthResponse(result.AccessToken, result.ExpiresInSeconds, result.State);
        }
        catch (Application.AppException)
        {
            DeleteRefreshCookie();
            throw;
        }
    }

    /// <summary>Revokes the sign-in and deletes the cookie. Always 204.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<NoContentResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], HttpContext.Client(), ct);
        DeleteRefreshCookie();
        return NoContent();
    }

    private void SetRefreshCookie(IssuedRefreshToken refresh) =>
        Response.Cookies.Append(RefreshCookie, refresh.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = refresh.ExpiresAt,
            IsEssential = true, // strictly necessary cookie
        });

    private void DeleteRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
        });
}
