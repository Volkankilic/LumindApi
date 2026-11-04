using System.Net;
using Chatbot.Api.Startup;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Security;
using Chatbot.Tests.TestSupport;

namespace Chatbot.Tests.Http;

public class ApiResponseHeaderTests
{
    [Theory]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/api/me")] // 401: even an error answer carries them
    [InlineData("POST", "/api/auth/code")] // 401 invalid code
    public async Task EveryApiAnswer_IsNotCached_NotSniffed_AndSendsNoReferrer(string method, string path)
    {
        using var factory = new ApiFactory();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = HttpHelpers.Json("""{"code":"AAAAAAAA"}""");
        }

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task AnAuthenticatedAnswerWithPersonalData_IsNoStore()
    {
        using var factory = new ApiFactory();
        var response = await factory.Authed().GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AnOversizedBody_IsRefusedBeforeItIsRead_WithTheUsualValidationAnswer()
    {
        using var factory = new ApiFactory();
        var huge = new ByteArrayContent(new byte[(64 * 1024) + 1]);
        huge.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await factory.Authed().PostAsync("/api/conversations/" + HttpHelpers.SomeConversation + "/messages", huge);

        await response.AssertProblem(HttpStatusCode.UnprocessableEntity, "validation-failed");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ABodyWithinTheLimit_IsStillAccepted()
    {
        using var factory = new ApiFactory();
        var ok = HttpHelpers.Json("{\"code\":\"" + new string('A', 5000) + "\"}"); // a silly but small body: judged by the sign-in, not the size
        var response = await factory.CreateClient().PostAsync("/api/auth/code", ok);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeletingTheAccount_ClearsTheRefreshCookie_WithTheSameAttributesItWasSetWith()
    {
        using var factory = new ApiFactory(settings: new() { ["Auth:CookieSecure"] = "true" });
        var response = await factory.Authed().DeleteAsync("/api/me");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("lumind_refresh=", StringComparison.Ordinal));
        Assert.Contains("path=/api/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }
}

public class ClientKeyTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")] // an IPv4 client seen through a dual-stack socket
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1:2:3:4", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("fd12:1234::7", "fd12:1234::/64")]
    public void IPv4IsKept_IPv6IsReducedToItsSlash64(string address, string expected) =>
        Assert.Equal(expected, ApiServices.RateLimitAddress(IPAddress.Parse(address)));

    [Fact]
    public void TwoDifferentSlash64s_AreDifferentClients() =>
        Assert.NotEqual(
            ApiServices.RateLimitAddress(IPAddress.Parse("2001:db8:1:2::1")),
            ApiServices.RateLimitAddress(IPAddress.Parse("2001:db8:1:3::1")));

    [Fact]
    public void NoAddress_IsOneBucket() => Assert.Equal("unknown", ApiServices.RateLimitAddress(null));
}

public class Ipv6RateLimitTests
{
    private static async Task<HttpStatusCode> Attempt(ApiFactory factory, string peer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code") { Content = HttpHelpers.Json("""{"code":"AAAAAAAA"}""") };
        request.Headers.Add("X-Test-Peer", peer);
        return (await factory.CreateClient().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task ChangingTheLower64Bits_DoesNotDodgeTheSignInLimit_ButAnotherSubscriberIsUnaffected()
    {
        using var factory = new ApiFactory(settings: new() { ["Auth:CodeAttempts"] = "3" });

        // One subscriber (one /64) tries a new address every time.
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await Attempt(factory, $"2001:db8:aaaa:1::{i:x}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Attempt(factory, "2001:db8:aaaa:1:dead:beef:0:99"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Attempt(factory, "2001:db8:aaaa:1:1234:5678:9abc:def0"));

        // Another /64 and an IPv4 client have their own budget.
        Assert.Equal(HttpStatusCode.Unauthorized, await Attempt(factory, "2001:db8:aaaa:2::1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Attempt(factory, "198.51.100.20"));
    }
}

public class FailureBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static AuthFailureBudget Budget(int threshold = 3, int minutes = 10) =>
        new(new AuthOptions { FailureBudgetThreshold = threshold, FailureBudgetWindowMinutes = minutes });

    [Fact]
    public void ItSaysSo_OnceWhenTheThresholdIsReached_NotOnEveryFailureAfterwards()
    {
        var budget = Budget(threshold: 3);
        Assert.False(budget.RecordFailure(T0));
        Assert.False(budget.RecordFailure(T0.AddSeconds(1)));
        Assert.True(budget.RecordFailure(T0.AddSeconds(2)));
        Assert.False(budget.RecordFailure(T0.AddSeconds(3)));
        Assert.False(budget.RecordFailure(T0.AddSeconds(4)));
    }

    [Fact]
    public void ItSpeaksAgain_InTheNextWindow_IfTheAttackGoesOn()
    {
        var budget = Budget(threshold: 2, minutes: 10);
        Assert.False(budget.RecordFailure(T0));
        Assert.True(budget.RecordFailure(T0.AddSeconds(1)));
        Assert.False(budget.RecordFailure(T0.AddMinutes(5)));
        // 11 minutes in: the first two failures have left the window, the one at +5 and this one are in it: the budget is
        // reached again and the earlier alert is a window old, so it speaks again, once.
        Assert.True(budget.RecordFailure(T0.AddMinutes(11)));
        Assert.False(budget.RecordFailure(T0.AddMinutes(11).AddSeconds(1)));
    }

    [Fact]
    public void SlowFailures_NeverReachTheThreshold()
    {
        var budget = Budget(threshold: 3, minutes: 10);
        for (var i = 0; i < 50; i++)
        {
            Assert.False(budget.RecordFailure(T0.AddMinutes(i * 6))); // at most two in any ten minutes
        }
    }

    [Fact]
    public void ZeroSwitchesItOff()
    {
        var budget = Budget(threshold: 0);
        Assert.False(budget.Enabled);
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(budget.RecordFailure(T0));
        }
    }

    [Fact]
    public async Task ItNeverBlocksAnyone_HundredsOfFailuresFromManyClients_StillGetTheNormalAnswer()
    {
        using var factory = new ApiFactory(settings: new()
        {
            ["Auth:CodeAttempts"] = "10000",
            ["Auth:FailureBudgetThreshold"] = "5",
        });

        for (var i = 0; i < 40; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code") { Content = HttpHelpers.Json("""{"code":"AAAAAAAA"}""") };
            request.Headers.Add("X-Test-Peer", $"198.51.100.{(i % 200) + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(request)).StatusCode);
        }
    }
}
