using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chatbot.Api.Controllers;
using Chatbot.Api.Security;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Admin;
using Chatbot.Domain;
using Chatbot.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chatbot.Tests.Http;

public sealed class StubAdminAccess : IAdminAccess
{
    public bool Allowed { get; set; } = true;

    public Task<bool> CheckAsync(Guid adminId, Guid familyId, CancellationToken ct) => Task.FromResult(Allowed);

    public Task<string?> UsernameAsync(Guid adminId, CancellationToken ct) => Task.FromResult<string?>("stub-admin");
}

public sealed class StubAdminAuthService : IAdminAuthService
{
    public bool FailRefresh { get; set; }

    public int RefreshCalls { get; private set; }

    public int LogoutCalls { get; private set; }

    public Task<AdminLoginResult> LoginAsync(string? username, string? password, ClientInfo client, CancellationToken ct) =>
        Task.FromResult(new AdminLoginResult("stub-mfa-token", 300));

    public Task<AdminAuthResult> CompleteAsync(string? mfaToken, string? code, string? recoveryCode, ClientInfo client, CancellationToken ct) =>
        Task.FromResult(Result());

    public Task<AdminAuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        RefreshCalls++;
        return FailRefresh ? throw AppException.Unauthorized() : Task.FromResult(Result());
    }

    public Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        LogoutCalls++;
        return Task.CompletedTask;
    }

    private static AdminAuthResult Result() =>
        new("stub-access-token", 600, "stub-admin", new IssuedRefreshToken("stub-refresh-token", DateTimeOffset.UtcNow.AddMinutes(30)));
}

/// <summary>The admin HTTP layer (tokens, policy, headers, cookies, limits) without a database.</summary>
public class AdminHttpTests
{
    private sealed class Rig : IDisposable
    {
        public StubAdminAccess Access { get; } = new();

        public StubAdminAuthService Auth { get; } = new();

        public ApiFactory Factory { get; }

        public Rig(string environment = "Testing", Dictionary<string, string?>? settings = null)
        {
            Factory = new ApiFactory(environment, settings, configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAdminAccess>(Access));
                services.Replace(ServiceDescriptor.Singleton<IAdminAuthService>(Auth));
            });
        }

        public HttpClient Client(string? bearer = null)
        {
            var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            if (bearer is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            return client;
        }

        public string AdminToken(Guid? adminId = null, Guid? sessionId = null) =>
            Factory.Services.GetRequiredService<IAdminTokenIssuer>().IssueAccess(adminId ?? Guid.NewGuid(), sessionId ?? Guid.NewGuid()).Token;

        public string MfaToken() => Factory.Services.GetRequiredService<IAdminTokenIssuer>().IssueMfa(Guid.NewGuid()).Token;

        public string ParticipantToken() => Factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(StubAuthService.ParticipantId).Token;

        public void Dispose() => Factory.Dispose();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string url, bool withHeader, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (withHeader)
        {
            request.Headers.Add(AdminSecurity.RequestHeader, "1");
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{AdminAuthController.RefreshCookie}={cookie}");
        }

        return request;
    }

    // ---------------------------------------------------------------- tokens do not cross

    [Fact]
    public async Task NoToken_AParticipantToken_AndAnMfaToken_DoNotOpenTheAdminApi()
    {
        using var rig = new Rig();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client().GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.ParticipantToken()).GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.MfaToken()).GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.ParticipantToken()).GetAsync("/api/admin/participants")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.ParticipantToken()).GetAsync("/api/admin/crisis")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.ParticipantToken()).PostAsync($"/api/admin/participants/{Guid.NewGuid()}/reset-code", null)).StatusCode);
    }

    [Fact]
    public async Task AnAdminToken_DoesNotOpenTheParticipantApi_NorTheMfaToken()
    {
        using var rig = new Rig();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.AdminToken()).GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.AdminToken()).GetAsync("/api/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(rig.MfaToken()).GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task ATokenSignedWithTheParticipantKey_ButTheAdminAudience_IsRefused()
    {
        using var rig = new Rig();
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "lumind-api",
            Audience = "lumind-admin",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("sid", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Researcher"),
            ]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('j', 48))), SecurityAlgorithms.HmacSha256), // Auth:JwtSigningKey of the test host
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(forged).GetAsync("/api/admin/me")).StatusCode);
    }

    [Fact]
    public async Task AnAdminTokenWithoutASessionClaim_OrWithTheWrongRole_IsRefused()
    {
        using var rig = new Rig();
        var handler = new JsonWebTokenHandler();
        string Make(params System.Security.Claims.Claim[] claims) => handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "lumind-api",
            Audience = "lumind-admin",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new System.Security.Claims.ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('a', 48))), SecurityAlgorithms.HmacSha256),
        });

        var noSession = Make(new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Researcher"));
        var wrongRole = Make(new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()), new System.Security.Claims.Claim("sid", Guid.NewGuid().ToString()), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Participant"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(noSession).GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(wrongRole).GetAsync("/api/admin/me")).StatusCode);
    }

    [Fact]
    public async Task ARealAdminToken_Works_NotCached_AndStopsWorkingWhenTheSessionOrTheAccountIsGone()
    {
        using var rig = new Rig();
        var token = rig.AdminToken();

        var ok = await rig.Client(token).GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("stub-admin", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("no-store", string.Join(",", ok.Headers.CacheControl?.ToString() ?? string.Empty), StringComparison.Ordinal);

        rig.Access.Allowed = false; // logged out, revoked, disabled: checked against the database on EVERY request
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(token).GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Client(token).GetAsync("/api/admin/stats/summary")).StatusCode);
    }

    [Fact]
    public async Task TheAccessTokenAndTheMfaToken_HaveTheirOwnAudiences_AndNoPersonalData()
    {
        using var rig = new Rig();
        var handler = new JsonWebTokenHandler();
        var access = handler.ReadJsonWebToken(rig.AdminToken());
        var mfa = handler.ReadJsonWebToken(rig.MfaToken());
        Assert.Contains("lumind-admin", access.Audiences);
        Assert.Contains("lumind-admin-mfa", mfa.Audiences);
        Assert.DoesNotContain(access.Claims, c => c.Type is "name" or "email" or "username" or "preferred_username");
        Assert.Equal("admin-totp", mfa.GetClaim("purpose").Value);
        Assert.True(mfa.ValidTo - mfa.ValidFrom <= TimeSpan.FromMinutes(6));
        Assert.True(access.ValidTo - access.ValidFrom <= TimeSpan.FromMinutes(11));
    }

    // ---------------------------------------------------------------- sign-in answers, cookie, CSRF header

    [Fact]
    public async Task Login_AnswersTotpRequired_WithNoCookieAndNoAccessToken()
    {
        using var rig = new Rig();
        var response = await rig.Client().PostAsync("/api/admin/auth/login", Json("""{"username":"x","password":"y"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("totp-required", body.RootElement.GetProperty("status").GetString());
        Assert.False(body.RootElement.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task TheTotpStep_IssuesTheAccessToken_AndTheRefreshCookieWithTheRightAttributes_NeverInTheBody()
    {
        using var rig = new Rig();
        var response = await rig.Client().PostAsync("/api/admin/auth/totp", Json("""{"mfaToken":"a","code":"123456"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("lumind_admin_refresh=stub-refresh-token", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/admin/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stub-refresh-token", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoRefreshCookies_LiveOnPathsThatDoNotOverlap()
    {
        Assert.Equal("/api/admin/auth", AdminAuthController.CookiePath);
        Assert.Equal("/api/auth", "/api/auth");
        Assert.False(AdminAuthController.CookiePath.StartsWith("/api/auth/", StringComparison.Ordinal));
        Assert.NotEqual(AuthController.RefreshCookie, AdminAuthController.RefreshCookie);
    }

    [Theory]
    [InlineData("/api/admin/auth/refresh")]
    [InlineData("/api/admin/auth/logout")]
    public async Task RefreshAndLogout_NeedTheCustomHeader_WithoutItNothingHappens(string url)
    {
        using var rig = new Rig();
        var withoutHeader = await rig.Client().SendAsync(Post(url, withHeader: false, cookie: "stub-refresh-token"));
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Contains("\"forbidden\"", await withoutHeader.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(withoutHeader.Headers.Contains("Set-Cookie")); // the cookie is neither renewed nor deleted
        Assert.Equal((0, 0), (rig.Auth.RefreshCalls, rig.Auth.LogoutCalls)); // the service was never reached

        var wrongValue = new HttpRequestMessage(HttpMethod.Post, url);
        wrongValue.Headers.Add(AdminSecurity.RequestHeader, "0");
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Client().SendAsync(wrongValue)).StatusCode);

        var withHeader = await rig.Client().SendAsync(Post(url, withHeader: true, cookie: "stub-refresh-token"));
        Assert.True(withHeader.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ARefreshThatFails_DeletesTheCookie_WithTheSameAttributes()
    {
        using var rig = new Rig();
        rig.Auth.FailRefresh = true;
        var response = await rig.Client().SendAsync(Post("/api/admin/auth/refresh", withHeader: true, cookie: "stale"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("lumind_admin_refresh=;", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/admin/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheParticipantRefreshEndpoint_DoesNotNeedTheHeader_AndTheAdminOneIsNotItsAlias()
    {
        using var rig = new Rig();
        // Participant refresh without a cookie is a plain 401 of its own flow (stub): nothing about the admin header.
        var participant = await rig.Client().PostAsync("/api/auth/refresh", null);
        Assert.NotEqual(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Client().PostAsync("/api/admin/refresh", null)).StatusCode);
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public async Task TheLogin_IsLimitedPerClient_AndTheTotpStepHasItsOwnBudget()
    {
        using var rig = new Rig();
        var client = rig.Client();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/auth/login", Json("""{"username":"x","password":"y"}"""))).StatusCode);
        }

        var limited = await client.PostAsync("/api/admin/auth/login", Json("""{"username":"x","password":"y"}"""));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Contains("rate-limited", await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The password budget is spent, the TOTP budget is not.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/auth/totp", Json("""{"mfaToken":"a","code":"123456"}"""))).StatusCode);
    }

    // ---------------------------------------------------------------- environment

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task TheDevelopmentTestAdminRoutes_DoNotExistOutsideDevelopment(string environment)
    {
        using var rig = new Rig(environment);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Client().PostAsync("/api/dev/test-admins", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Client().DeleteAsync($"/api/dev/test-admins/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task TheAdminApi_IsNotPartOfThePublishedOpenApiDocument()
    {
        using var rig = new Rig("Development");
        var document = await rig.Client().GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/conversations", document, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheAdminKey_OrWithTheParticipantKey_TheApplicationDoesNotStart_NamingTheSettingOnly()
    {
        foreach (var value in new[] { string.Empty, new string('j', 48) })
        {
            using var rig = new Rig(settings: new() { ["Admin:JwtSigningKey"] = value });
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var client = rig.Client();
                await client.GetAsync("/api/health");
            });
            var text = ex.ToString();
            Assert.Contains("Admin:JwtSigningKey", text, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('j', 48), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheAdminRole_IsNotTheParticipantRole_AndSharesItsNameWithTheAuditActor()
    {
        Assert.Equal("Researcher", AdminSecurity.Role);
        Assert.NotEqual(Roles.Participant, AdminSecurity.Role);
        Assert.Equal(AdminSecurity.Role, ActorType.Researcher.ToString()); // the audit actor type and the role share the name
    }
}
