using System.Security.Claims;
using System.Text;
using Chatbot.Api.Http;
using Chatbot.Application;
using Chatbot.Application.Admin;
using Chatbot.Application.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chatbot.Api.Security;

public static class AdminSecurity
{
    /// <summary>The authentication scheme of the admin access token (its own key and audience).</summary>
    public const string Scheme = "AdminBearer";

    /// <summary>A valid admin token of an active account (with TOTP) and a live session, checked against the database on every request.</summary>
    public const string Policy = "Admin";

    public const string Role = "Researcher";

    /// <summary>Claim holding the session (refresh token family): a logout, a revoke or a disabled account ends the access token at once.</summary>
    public const string SessionClaim = "sid";

    /// <summary>
    /// The custom request header that the refresh and logout calls must carry (an extra CSRF defence on top of SameSite=Strict:
    /// a cross-site page cannot add a custom header without a CORS preflight that this API does not allow).
    /// </summary>
    public const string RequestHeader = "X-Lumind-Admin";

    public static Guid AdminId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;

    public static Guid SessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SessionClaim), out var id) ? id : Guid.Empty;
}

/// <summary>
/// Admin tokens: HS256 with <c>Admin:JwtSigningKey</c> (not the participant key). The access token has audience
/// <c>lumind-admin</c>; the "password accepted, TOTP pending" token has its own audience and a purpose claim, so neither can
/// be used for the other thing, and a participant token is not valid for either. No name, e-mail or secret is put into a token.
/// </summary>
public sealed class AdminTokenIssuer(AdminAuthOptions options, TimeProvider clock) : IAdminTokenIssuer
{
    private const string PurposeClaim = "purpose";
    private const string MfaPurpose = "admin-totp";
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public (string Token, DateTimeOffset ExpiresAt) IssueAccess(Guid adminId, Guid familyId)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(options.AccessTokenMinutes);
        return (Create(options.Audience, expiresAt, now,
        [
            new Claim(JwtRegisteredClaimNames.Sub, adminId.ToString()),
            new Claim(AdminSecurity.SessionClaim, familyId.ToString()),
            new Claim(ClaimTypes.Role, AdminSecurity.Role),
        ]), expiresAt);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueMfa(Guid adminId)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(options.MfaTokenMinutes);
        return (Create(options.MfaAudience, expiresAt, now,
        [
            new Claim(JwtRegisteredClaimNames.Sub, adminId.ToString()),
            new Claim(PurposeClaim, MfaPurpose),
        ]), expiresAt);
    }

    public async Task<Guid?> ValidateMfaAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return null;
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.MfaAudience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = Key(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(10),
        });
        if (!result.IsValid
            || (result.Claims.TryGetValue(PurposeClaim, out var purpose) ? purpose as string : null) != MfaPurpose
            || !Guid.TryParse(result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub) ? sub as string : null, out var id))
        {
            return null;
        }

        return id;
    }

    private string Create(string audience, DateTimeOffset expiresAt, DateTimeOffset now, Claim[] claims) =>
        _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(Key(), SecurityAlgorithms.HmacSha256),
        });

    private SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(options.JwtSigningKey));
}

public sealed record AdminAccessRequirement : IAuthorizationRequirement;

/// <summary>Fails (a plain 401) unless the account is active with TOTP and the session of the token is still alive.</summary>
public sealed class AdminAccessHandler(IAdminAccess access) : AuthorizationHandler<AdminAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminAccessRequirement requirement)
    {
        var adminId = context.User.AdminId();
        var sessionId = context.User.SessionId();
        if (adminId == Guid.Empty || sessionId == Guid.Empty)
        {
            context.Fail();
            return;
        }

        var ct = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        if (await access.CheckAsync(adminId, sessionId, ct))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail();
        }
    }
}

/// <summary>The "custom header" rule for refresh and logout, as an endpoint filter-free helper used by the controller.</summary>
public static class AdminRequestHeader
{
    public static void Require(HttpRequest request)
    {
        if (request.Headers[AdminSecurity.RequestHeader] != "1")
        {
            throw AppException.Forbidden();
        }
    }
}
