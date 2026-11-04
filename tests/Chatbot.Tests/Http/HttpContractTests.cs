using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Domain;
using Chatbot.Domain.Rules;
using Chatbot.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chatbot.Tests.Http;

internal static class HttpHelpers
{
    public static HttpClient Authed(this ApiFactory factory, Guid? participantId = null)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(participantId ?? StubAuthService.ParticipantId).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<JsonElement> Body(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task AssertProblem(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        var body = await response.Body();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    public static readonly Guid SomeConversation = Guid.Parse("22222222-2222-2222-2222-222222222222");
}

public class HealthAndEnvironmentTests
{
    [Fact]
    public async Task Health_WhenReady_IsOk_AndInProductionSaysNothingMore()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Body();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("schema", out _), "no schema detail outside Development");
    }

    [Fact]
    public async Task Health_WhenTheSchemaIsNotReady_Is503NotReady_WithoutDetailOutsideDevelopment()
    {
        using var factory = new ApiFactory();
        factory.Schema.Status = new SchemaStatus(false, 1, null, "Apply the scripts in api/db/: expected schema version 1, found none.");
        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("not-ready", text, StringComparison.Ordinal);
        Assert.DoesNotContain("api/db", text, StringComparison.Ordinal);
        Assert.DoesNotContain("expected", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_InDevelopment_ShowsTheSchemaVersions()
    {
        using var factory = new ApiFactory("Development");
        factory.Schema.Status = new SchemaStatus(false, 2, 1, "Apply the scripts in api/db/: expected schema version 2, found 1.");
        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var schema = (await response.Body()).GetProperty("schema");
        Assert.Equal(2, schema.GetProperty("expected").GetInt32());
        Assert.Equal(1, schema.GetProperty("found").GetInt32());
        Assert.Contains("api/db", schema.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOpenApiDocument_ExistsInDevelopmentOnly()
    {
        using var production = new ApiFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await production.CreateClient().GetAsync("/openapi/v1.json")).StatusCode);

        using var development = new ApiFactory("Development");
        var response = await development.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await response.Body()).GetProperty("paths");
        foreach (var path in new[] { "/api/auth/code", "/api/auth/refresh", "/api/auth/logout", "/api/me", "/api/consents", "/api/me/nickname", "/api/conversations", "/api/conversations/{id}", "/api/conversations/{id}/end", "/api/conversations/{id}/messages", "/api/health" })
        {
            Assert.True(paths.TryGetProperty(path, out _), $"missing {path}");
        }

        Assert.True(paths.GetProperty("/api/conversations/{id}/messages").GetProperty("post").GetProperty("responses").GetProperty("200")
            .GetProperty("content").TryGetProperty("text/event-stream", out _), "the SSE endpoint documents text/event-stream");
    }

    [Fact]
    public async Task AnUnknownRoute_IsAProblemDetails404_NotFound()
    {
        using var factory = new ApiFactory();
        await (await factory.CreateClient().GetAsync("/api/nope")).AssertProblem(HttpStatusCode.NotFound, "not-found");
    }
}

public class AuthorizationTests
{
    // (method, path) of every endpoint that needs a signed-in participant. The table drives the 401 / 403 checks below.
    public static TheoryData<string, string> Protected => new()
    {
        { "GET", "/api/me" },
        { "POST", "/api/consents" },
        { "PUT", "/api/me/nickname" },
        { "DELETE", "/api/me" },
        { "POST", "/api/conversations" },
        { "GET", "/api/conversations" },
        { "GET", "/api/conversations/22222222-2222-2222-2222-222222222222" },
        { "POST", "/api/conversations/22222222-2222-2222-2222-222222222222/end" },
        { "POST", "/api/conversations/22222222-2222-2222-2222-222222222222/messages" },
    };

    // Endpoints that also need a current notice and a valid consent (the OnboardedParticipant policy).
    public static TheoryData<string, string> NeedConsent => new()
    {
        { "POST", "/api/conversations" },
        { "GET", "/api/conversations" },
        { "GET", "/api/conversations/22222222-2222-2222-2222-222222222222" },
        { "POST", "/api/conversations/22222222-2222-2222-2222-222222222222/end" },
        { "POST", "/api/conversations/22222222-2222-2222-2222-222222222222/messages" },
    };

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method is "POST" or "PUT" ? HttpHelpers.Json("{}") : null,
    };

    [Theory]
    [MemberData(nameof(Protected))]
    public async Task WithoutAToken_EveryProtectedEndpoint_Is401(string method, string path)
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().SendAsync(Request(method, path));
        await response.AssertProblem(HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protected))]
    public async Task ARevokedOrDeletedParticipant_IsLockedOutAtOnce_Even_WithAValidToken(string method, string path)
    {
        using var factory = new ApiFactory();
        factory.Access.Status = AccessStatus.Denied;
        var response = await factory.Authed().SendAsync(Request(method, path));
        await response.AssertProblem(HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [MemberData(nameof(NeedConsent))]
    public async Task WithoutConsent_TheChatEndpoints_Are403ConsentRequired(string method, string path)
    {
        using var factory = new ApiFactory();
        factory.Access.Status = AccessStatus.NotOnboarded;
        var response = await factory.Authed().SendAsync(Request(method, path));
        await response.AssertProblem(HttpStatusCode.Forbidden, "consent-required");
    }

    [Fact]
    public async Task WithoutConsent_TheOnboardingEndpointsStillWork()
    {
        using var factory = new ApiFactory();
        factory.Access.Status = AccessStatus.NotOnboarded;
        var client = factory.Authed();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/consents", HttpHelpers.Json("""{"records":[{"type":"privacyNotice","version":"0.1-taslak","granted":true}]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/me/nickname", HttpHelpers.Json("""{"nickname":"Mavi"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task TheAuthEndpointsAreOpen_NoTokenNeeded()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    // ---------------------------------------------------------------- the JWT itself

    private static string Token(
        string? key = null, string issuer = "lumind-api", string audience = "lumind-client",
        DateTime? expires = null, string algorithm = SecurityAlgorithms.HmacSha256, Guid? sub = null)
    {
        var signing = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? new string('j', 48))), algorithm);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-20),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Subject = new ClaimsIdentity([new Claim("sub", (sub ?? StubAuthService.ParticipantId).ToString()), new Claim(ClaimTypes.Role, "Participant")]),
            SigningCredentials = signing,
        });
    }

    private static async Task<HttpStatusCode> GetMe(ApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.GetAsync("/api/me")).StatusCode;
    }

    [Fact]
    public async Task AWellFormedToken_IsAccepted()
    {
        using var factory = new ApiFactory();
        Assert.Equal(HttpStatusCode.OK, await GetMe(factory, Token()));
    }

    [Fact]
    public async Task ExpiredWrongIssuerWrongAudienceWrongKeyAndTamperedTokens_Are401()
    {
        using var factory = new ApiFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, Token(expires: DateTime.UtcNow.AddMinutes(-5))));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, Token(issuer: "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, Token(audience: "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, Token(key: new string('x', 48))));

        var tampered = Token();
        tampered = tampered[..^3] + (tampered[^3] == 'A' ? "BBB" : "AAA");
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, tampered));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, "not-a-token"));
    }

    [Fact]
    public async Task ATokenSignedWithADifferentAlgorithm_IsRejected()
    {
        using var factory = new ApiFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, Token(algorithm: SecurityAlgorithms.HmacSha512, key: new string('j', 80))));
    }

    [Fact]
    public async Task AnUnsignedToken_AlgNone_IsRejected()
    {
        using var factory = new ApiFactory();
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsigned = B64("""{"alg":"none","typ":"JWT"}""") + "." +
            B64($$"""{"iss":"lumind-api","aud":"lumind-client","sub":"{{StubAuthService.ParticipantId}}","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""") + ".";
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, unsigned));
    }

    [Fact]
    public async Task ATokenWithoutTheParticipantRole_IsRejected()
    {
        using var factory = new ApiFactory();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "lumind-api",
            Audience = "lumind-client",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Subject = new ClaimsIdentity([new Claim("sub", StubAuthService.ParticipantId.ToString())]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('j', 48))), SecurityAlgorithms.HmacSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMe(factory, token));
    }

    [Fact]
    public void TheIssuedToken_HoldsOnlySubjectAndRole_NeverACodeOrANickname_AndLivesFifteenMinutes()
    {
        using var factory = new ApiFactory();
        var (token, expiresAt) = factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(StubAuthService.ParticipantId);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var names = jwt.Claims.Select(c => c.Type).ToHashSet();
        Assert.Contains("sub", names);
        Assert.Equal(StubAuthService.ParticipantId.ToString(), jwt.Subject);
        Assert.All(names, n => Assert.Contains(n, new[] { "sub", "role", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "iss", "aud", "exp", "nbf", "iat" }));
        Assert.Contains(jwt.Claims, c => c.Value == "Participant");
        Assert.InRange((expiresAt - DateTimeOffset.UtcNow).TotalMinutes, 14, 15.1);
        Assert.Equal("HS256", jwt.Header.Alg);
    }
}

public class AuthEndpointTests
{
    [Fact]
    public async Task AnInvalidCode_IsTheSame401_WhateverTheReason()
    {
        using var factory = new ApiFactory();
        factory.Auth.SignIn = _ => throw AppException.InvalidCode();
        var client = factory.CreateClient();

        var bodies = new List<string>();
        foreach (var code in new[] { "ABCD2345", "x", "", "ABCD-234O" })
        {
            var response = await client.PostAsync("/api/auth/code", HttpHelpers.Json($$"""{"code":"{{code}}"}"""));
            await response.AssertProblem(HttpStatusCode.Unauthorized, "invalid-code");
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
        Assert.DoesNotContain("ABCD", bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulSignIn_ReturnsTheTokenAndState_AndTheRefreshTokenOnlyAsAnHttpOnlyCookie()
    {
        using var factory = new ApiFactory();
        factory.Auth.SignIn = _ => StubAuthService.Success();
        var response = await factory.CreateClient().PostAsync("/api/auth/code", HttpHelpers.Json("""{"code":"ABCD2345"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refresh-token-value", text, StringComparison.Ordinal); // never in the body
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("access-token-value", body.GetProperty("accessToken").GetString());
        Assert.Equal(900, body.GetProperty("expiresInSeconds").GetInt32());
        var state = body.GetProperty("state");
        Assert.Equal(StubAuthService.ParticipantId.ToString(), state.GetProperty("participantId").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("nickname").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("noticeAcknowledgedVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("consent").ValueKind);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("lumind_refresh=refresh-token-value", cookie, StringComparison.Ordinal);
        var lower = cookie.ToLowerInvariant();
        Assert.Contains("httponly", lower, StringComparison.Ordinal);
        Assert.Contains("secure", lower, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", lower, StringComparison.Ordinal);
        Assert.Contains("path=/api/auth", lower, StringComparison.Ordinal);
        Assert.Contains("expires=", lower, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStateJson_IsCamelCase_WithTheClientsExactShape()
    {
        using var factory = new ApiFactory();
        factory.Participants.State = new ParticipantState(StubAuthService.ParticipantId, "Deneme", "0.1-taslak", new ConsentState("0.1-taslak", true, false));
        var response = await factory.Authed().GetAsync("/api/me");
        var body = await response.Body();

        Assert.Equal(["participantId", "nickname", "noticeAcknowledgedVersion", "consent"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["version", "processing", "researchUse"], body.GetProperty("consent").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(body.GetProperty("consent").GetProperty("processing").GetBoolean());
    }

    [Fact]
    public async Task Refresh_WithoutAValidCookie_Is401_AndTheCookieIsDeleted()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().PostAsync("/api/auth/refresh", null);
        await response.AssertProblem(HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains("expires=", string.Join(";", response.Headers.GetValues("Set-Cookie")).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_ReadsTheCookie_AndRotatesIt()
    {
        using var factory = new ApiFactory();
        string? seen = null;
        factory.Auth.Refresh = token =>
        {
            seen = token;
            return StubAuthService.Success();
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", "lumind_refresh=the-old-token");
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("the-old-token", seen);
        Assert.Contains("lumind_refresh=refresh-token-value", string.Join(";", response.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_IsAlways204_RevokesTheTokenAndDeletesTheCookie()
    {
        using var factory = new ApiFactory();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", "lumind_refresh=some-token");
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["some-token"], factory.Auth.LoggedOut);
        var cookie = string.Join(";", response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("lumind_refresh=;", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCookieSecureFlag_CanBeSwitchedOffForLocalHttp()
    {
        using var factory = new ApiFactory(settings: new() { ["Auth:CookieSecure"] = "false" });
        factory.Auth.SignIn = _ => StubAuthService.Success();
        var response = await factory.CreateClient().PostAsync("/api/auth/code", HttpHelpers.Json("""{"code":"ABCD2345"}"""));
        var cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.DoesNotContain("secure", cookie.Replace("samesite", string.Empty), StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCodeEndpoint_IsRateLimitedPerClient_With429RetryAfterAndTheCode()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var ok = await client.PostAsync("/api/auth/code", HttpHelpers.Json("""{"code":"AAAAAAAA"}"""));
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var limited = await client.PostAsync("/api/auth/code", HttpHelpers.Json("""{"code":"AAAAAAAA"}"""));
        await limited.AssertProblem(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(limited.Headers.RetryAfter?.Delta?.TotalSeconds is > 0 and <= 30, "Retry-After is set");
        Assert.True((await limited.Body()).GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task ASpoofedForwardedForHeader_IsIgnoredByDefault_SoTheRateLimitCannotBeDodged()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/code") { Content = HttpHelpers.Json("""{"code":"AAAAAAAA"}""") };
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            last = await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }
}

public class OnboardingValidationTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("""{"records":[{"type":"nonsense","version":"x","granted":true}]}""")]
    [InlineData("""{"records":[{"type":7,"version":"x","granted":true}]}""")]
    public async Task AMalformedConsentBody_Is422ValidationFailed_WithoutEchoingIt(string body)
    {
        using var factory = new ApiFactory();
        var response = await factory.Authed().PostAsync("/api/consents", HttpHelpers.Json(body));
        await response.AssertProblem(HttpStatusCode.UnprocessableEntity, "validation-failed");
        Assert.DoesNotContain("nonsense", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentRecords_Missing_IsValidationFailed()
    {
        using var factory = new ApiFactory();
        var response = await factory.Authed().PostAsync("/api/consents", HttpHelpers.Json("{}"));
        await response.AssertProblem(HttpStatusCode.UnprocessableEntity, "validation-failed");
    }

    [Fact]
    public async Task TheConsentTypes_AreCamelCaseStrings()
    {
        using var factory = new ApiFactory();
        var response = await factory.Authed().PostAsync("/api/consents", HttpHelpers.Json(
            """{"records":[{"type":"explicitConsent","version":"0.1-taslak","granted":true},{"type":"researchUse","version":"0.1-taslak","granted":false}]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class ErrorHandlingTests
{
    [Fact]
    public async Task AnUnexpectedFailure_Is500WithAGenericProblem_NoDetail()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => throw new InvalidOperationException("SQL: select * from messages where content = 'gizli metin'; at Chatbot.Secret.Method()");
        var response = await factory.Authed().PostAsync(
            $"/api/conversations/{HttpHelpers.SomeConversation}/messages",
            HttpHelpers.Json($$"""{"text":"merhaba","clientMessageId":"{{Guid.NewGuid()}}"}"""));

        await response.AssertProblem(HttpStatusCode.InternalServerError, "unknown");
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("select", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gizli", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Chatbot.Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLog_OfAnUnexpectedFailure_HasTheExceptionTypeOnly()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => throw new InvalidOperationException("Key (token_hash)=(abc123secret) already exists");
        await factory.Authed().PostAsync(
            $"/api/conversations/{HttpHelpers.SomeConversation}/messages",
            HttpHelpers.Json($$"""{"text":"merhaba","clientMessageId":"{{Guid.NewGuid()}}"}"""));

        var rendered = string.Join("\n", factory.Logs.Rendered());
        Assert.Contains("InvalidOperationException", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token_hash", rendered, StringComparison.Ordinal);
    }
}
