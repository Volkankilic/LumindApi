using System.Net;
using Chatbot.Domain;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Integration;

/// <summary>Sign-in, refresh rotation, logout and the single active session, against the real database.</summary>
public class AuthIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    [PostgresFact]
    public async Task SignIn_WithAValidCode_ReturnsTheStateAndAHardenedRefreshCookie_AndActivatesTheParticipant()
    {
        var participant = await host.CreateParticipantAsync();
        var (response, cookie) = await host.Factory.PostCodeAsync(participant.Code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal(participant.Id, body.GetProperty("state").GetProperty("participantId").GetGuid());
        Assert.False(body.TryGetProperty("refreshToken", out _), "the refresh token travels only as a cookie");

        var setCookie = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("lumind_refresh=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", setCookie, StringComparison.OrdinalIgnoreCase);

        await using var db = TestDatabase.NewContext();
        Assert.Equal(ParticipantStatus.Active, (await db.Participants.AsNoTracking().SingleAsync(p => p.Id == participant.Id)).Status);

        // Only a hash is stored, never the cookie value.
        var tokens = await db.RefreshTokens.AsNoTracking().Where(t => t.ParticipantId == participant.Id).ToListAsync();
        Assert.Single(tokens);
        Assert.NotNull(cookie);
        Assert.NotEqual(cookie, tokens[0].TokenHash);
        Assert.DoesNotContain(cookie, tokens[0].TokenHash, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task SignIn_AcceptsTheCodeTheWayItIsTyped()
    {
        var participant = await host.CreateParticipantAsync();
        var typed = participant.Code.ToLowerInvariant().Insert(4, "-");
        var (response, _) = await host.Factory.PostCodeAsync(typed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task EveryBadCode_IsTheSame401()
    {
        var revoked = await host.CreateParticipantAsync(ParticipantStatus.Revoked);
        var deleted = await host.CreateParticipantAsync(ParticipantStatus.Deleted);

        foreach (var code in new[] { IntegrationHost.NewCode(), "ABC", string.Empty, revoked.Code, deleted.Code })
        {
            var (response, cookie) = await host.Factory.PostCodeAsync(code);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid-code", (await response.ReadJsonAsync()).GetProperty("code").GetString());
            Assert.Null(cookie);
        }
    }

    [PostgresFact]
    public async Task Refresh_RotatesTheToken_AndAReusedOldTokenRevokesTheWholeSignIn()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);

        var first = await host.Factory.RefreshAsync(session.RefreshCookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = IntegrationClient.ExtractRefreshCookie(first);
        Assert.NotNull(rotated);
        Assert.NotEqual(session.RefreshCookie, rotated);
        Assert.Equal(participant.Id, (await first.ReadJsonAsync()).GetProperty("state").GetProperty("participantId").GetGuid());

        // The spent (old) token comes back: 401, and the new one dies with it.
        var reused = await host.Factory.RefreshAsync(session.RefreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.RefreshAsync(rotated)).StatusCode);

        await using var db = TestDatabase.NewContext();
        Assert.All(await db.RefreshTokens.AsNoTracking().Where(t => t.ParticipantId == participant.Id).ToListAsync(),
            t => Assert.NotNull(t.RevokedAt));
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.ActorId == participant.Id && a.Action == "auth.refresh.reuse-detected"));
    }

    [PostgresFact]
    public async Task Refresh_WithoutOrWithAnUnknownCookie_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.RefreshAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.RefreshAsync("nonsense-token")).StatusCode);
    }

    [PostgresFact]
    public async Task ASecondSignIn_EndsTheFirstSession()
    {
        var participant = await host.CreateParticipantAsync();
        var first = await host.Factory.SignInAsync(participant);
        var second = await host.Factory.SignInAsync(participant);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.RefreshAsync(first.RefreshCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Factory.RefreshAsync(second.RefreshCookie)).StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentRefreshesWithTheSameToken_OnlyOneSucceeds()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.Factory.RefreshAsync(session.RefreshCookie)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [PostgresFact]
    public async Task Logout_RevokesTheSignIn_AndClearsTheCookie()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"lumind_refresh={session.RefreshCookie}");
        var response = await host.Factory.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.RefreshAsync(session.RefreshCookie)).StatusCode);
    }

    [PostgresFact]
    public async Task SignIn_IsRateLimited_PerClient()
    {
        using var limited = host.NewFactory(new() { ["Auth:CodeAttempts"] = "3" });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await limited.PostCodeAsync(IntegrationHost.NewCode())).Response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }
}
