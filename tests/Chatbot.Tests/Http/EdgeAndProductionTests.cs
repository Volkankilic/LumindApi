using System.Net;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Security;
using Chatbot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chatbot.Tests.Http;

/// <summary>
/// The API sits behind the client's Caddy (single origin). Caddy overwrites X-Forwarded-For with the one real client
/// address, and the API trusts forwarded headers ONLY from Caddy's private network, with a limit of one hop.
/// These tests prove the two ways to get it wrong are both closed: everybody collapsing into one proxy address, and
/// a forged header being believed. The peer address is simulated with X-Test-Peer (TestServer has no socket).
/// </summary>
public class ForwardedHeadersTrustTests
{
    // What the deployment configures (docs/yayin-railway.md): Railway's private network, IPv6 and IPv4.
    private static readonly Dictionary<string, string?> PrivateNetwork = new()
    {
        ["ForwardedHeaders:KnownNetworks:0"] = "fd00::/8",
        ["ForwardedHeaders:KnownNetworks:1"] = "10.0.0.0/8",
        ["Auth:CodeAttempts"] = "100",
    };

    private const string Caddy = "fd12:1234::7"; // a private-network peer: trusted
    private const string Stranger = "198.51.100.7"; // anybody else: not trusted

    private static async Task<string?> SignInAndGetClientIp(ApiFactory factory, string peer, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code") { Content = HttpHelpers.Json("""{"code":"AAAAAAAA"}""") };
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        factory.Auth.SignIn = _ => StubAuthService.Success();
        await factory.CreateClient().SendAsync(request);
        lock (factory.Auth.ClientIps)
        {
            return factory.Auth.ClientIps[^1];
        }
    }

    [Fact]
    public async Task ByDefault_NoForwardedHeaderIsBelieved_TheAddressIsThePeer()
    {
        using var factory = new ApiFactory();
        Assert.Equal(Caddy, await SignInAndGetClientIp(factory, Caddy, "6.6.6.6"));
        Assert.Equal(Stranger, await SignInAndGetClientIp(factory, Stranger, "6.6.6.6"));
    }

    [Fact]
    public async Task FromCaddysNetwork_TheSingleAddressItWrote_BecomesTheClientAddress()
    {
        using var factory = new ApiFactory(settings: PrivateNetwork);
        // not the proxy's own address (that would put every client in one rate-limit bucket and one ip_hash)
        Assert.Equal("203.0.113.9", await SignInAndGetClientIp(factory, Caddy, "203.0.113.9"));
        Assert.Equal("203.0.113.9", await SignInAndGetClientIp(factory, "10.20.30.40", "203.0.113.9")); // IPv4 private network too
    }

    [Fact]
    public async Task FromCaddysNetwork_OnlyTheLastHopCounts_AForgedLeadingValueIsNotBelieved()
    {
        using var factory = new ApiFactory(settings: PrivateNetwork);
        // limit of 1: the rightmost value (the one the trusted proxy wrote) wins, whatever the client prepended
        Assert.Equal("203.0.113.9", await SignInAndGetClientIp(factory, Caddy, "6.6.6.6, 7.7.7.7, 203.0.113.9"));
    }

    [Fact]
    public async Task FromAnyoneElse_AForgedHeaderIsIgnored_EvenWhenTheNetworkIsConfigured()
    {
        using var factory = new ApiFactory(settings: PrivateNetwork);
        Assert.Equal(Stranger, await SignInAndGetClientIp(factory, Stranger, "203.0.113.9"));
        Assert.Equal("100.64.1.1", await SignInAndGetClientIp(factory, "100.64.1.1", "203.0.113.9")); // outside the configured ranges
    }

    [Fact]
    public async Task TheRateLimit_FollowsTheRealClient_NotTheProxy_AndCannotBeDodgedByForging()
    {
        // 3 sign-in attempts per window.
        var settings = new Dictionary<string, string?>(PrivateNetwork) { ["Auth:CodeAttempts"] = "3" };
        using var factory = new ApiFactory(settings: settings);

        async Task<HttpStatusCode> Attempt(string peer, string? forwardedFor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code") { Content = HttpHelpers.Json("""{"code":"AAAAAAAA"}""") };
            request.Headers.Add("X-Test-Peer", peer);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            return (await factory.CreateClient().SendAsync(request)).StatusCode;
        }

        // (1) Behind Caddy: client A uses up its 3 attempts...
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await Attempt(Caddy, "203.0.113.1"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Attempt(Caddy, "203.0.113.1"));
        // ...and client B, coming through the SAME proxy address, is not punished for it (no collapse into one bucket).
        Assert.Equal(HttpStatusCode.Unauthorized, await Attempt(Caddy, "203.0.113.2"));

        // (2) A stranger rotating forged addresses in every request still hits the limit of ITS real address.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add(await Attempt(Stranger, $"203.0.113.{100 + i}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public void TheIpHash_FollowsTheClientAddress_IsKeyedAndNeverTheRawAddress()
    {
        var hasher = new SecretHasher(new AuthOptions { CodePepper = new string('p', 40) }, new PrivacyOptions { IpHashKey = new string('k', 40) });
        var other = new SecretHasher(new AuthOptions { CodePepper = new string('p', 40) }, new PrivacyOptions { IpHashKey = new string('z', 40) });

        var a = hasher.HashIp("203.0.113.1");
        Assert.Equal(a, hasher.HashIp("203.0.113.1"));
        Assert.NotEqual(a, hasher.HashIp("203.0.113.2")); // two clients behind one proxy stay distinguishable
        Assert.NotEqual(a, other.HashIp("203.0.113.1")); // keyed: another key, another hash
        Assert.DoesNotContain("203.0.113.1", a, StringComparison.Ordinal);
        Assert.Null(hasher.HashIp(null));
    }
}

public class ProductionStartupTests
{
    [Fact]
    public async Task InProduction_ASessionCookieThatIsNotSecure_StopsTheApplication_NamingTheSettingOnly()
    {
        var text = await FailedStart.TextAsync(() => new ApiFactory("Production", new() { ["Auth:CookieSecure"] = "false" }));
        Assert.Contains("Auth:CookieSecure must be true in Production", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InProduction_TheNoopCrisisDetector_StopsTheApplication_WithAClearMessage_AndNoSecret()
    {
        var text = await FailedStart.TextAsync(() => new ApiFactory(
            "Production",
            configure: services => services.Replace(ServiceDescriptor.Singleton<ICrisisDetector>(new NoopCrisisDetector()))));
        Assert.Contains("a production-grade (rule based) crisis detector is required in Production", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestEncryption.KeyBase64, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task OutsideProduction_TheCheckDoesNotApply_SoTestHostsWithTheirOwnDetectorStillStart(string environment)
    {
        using var factory = new ApiFactory(
            environment,
            configure: services => services.Replace(ServiceDescriptor.Singleton<ICrisisDetector>(new NoopCrisisDetector())));
        var response = await factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InProduction_ASecureCookie_Starts_AndTheHealthAnswerIsJustOk()
    {
        using var factory = new ApiFactory("Production");
        var response = await factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public void OutsideProduction_AnInsecureCookie_IsStillAllowed_ForLocalHttp(string environment)
    {
        var problems = OptionsValidator.Validate(
            new AuthOptions { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40), CookieSecure = false },
            TestEncryption.Privacy(),
            new DocumentsOptions(),
            new ChatOptions(),
            "Host=x",
            [],
            requireDatabase: true,
            production: environment == "Production");
        Assert.Empty(problems);
    }

    [Fact]
    public void TheValidator_RefusesAnInsecureCookieInProductionOnly()
    {
        var insecure = new AuthOptions { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40), CookieSecure = false };
        var problems = OptionsValidator.Validate(insecure, TestEncryption.Privacy(), new DocumentsOptions(), new ChatOptions(), "Host=x", [], true, production: true);
        Assert.Contains(problems, p => p.Contains("Auth:CookieSecure", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains(new string('p', 40), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/dev/test-participants")]
    [InlineData("/openapi/v1.json")]
    public async Task InProduction_TheDevelopmentRoutesAndTheOpenApiDocumentDoNotExist(string path)
    {
        using var factory = new ApiFactory("Production");
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path, HttpHelpers.Json("{}"))).StatusCode);
    }

    [Fact]
    public async Task InProduction_AnUnexpectedFailureShowsNoDetail()
    {
        using var factory = new ApiFactory("Production");
        factory.Auth.SignIn = _ => throw new InvalidOperationException("SECRET-INTERNAL-DETAIL at C:\\src\\Secret.cs");
        var response = await factory.CreateClient().PostAsync("/api/auth/code", HttpHelpers.Json("""{"code":"AAAAAAAA"}"""));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperation", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }
}
