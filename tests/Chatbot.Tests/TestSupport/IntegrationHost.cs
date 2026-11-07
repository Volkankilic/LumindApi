using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chatbot.Tests.TestSupport;

/// <summary>Flags the one marker phrase, so the crisis flow can be tried against the real pipeline (the Development detector is not registered in tests).</summary>
public sealed class MarkerCrisisDetector : ICrisisDetector
{
    public const string Marker = "KRIZ-TEST-IFADESI";

    public CrisisAssessment Assess(string text) =>
        text.Contains(Marker, StringComparison.Ordinal) ? CrisisAssessment.Of(CrisisCategory.Unknown) : CrisisAssessment.None;
}

/// <summary>
/// The real API pipeline on the real (shared) database. Every participant is created here with an `itest-` label, and
/// at the end exactly those participants (and the rows that belong to them) are deleted in foreign key order.
/// The seed participants are never used.
/// </summary>
public sealed class IntegrationHost : IAsyncLifetime
{
    private static readonly AuthOptions TestAuth = new() { CodePepper = ApiFactory.DefaultSettings()["Auth:CodePepper"]! };
    private static readonly PrivacyOptions TestPrivacy = new() { IpHashKey = ApiFactory.DefaultSettings()["Privacy:IpHashKey"]! };
    private static readonly SecretHasher Hasher = new(TestAuth, TestPrivacy);

    private readonly List<Guid> _participants = [];
    private readonly object _gate = new();

    public ApiFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        if (!TestDatabase.EnsureAvailable())
        {
            return Task.CompletedTask; // the tests are skipped; nothing to host
        }

        Factory = NewFactory();
        return Task.CompletedTask;
    }

    public ApiFactory NewFactory(Dictionary<string, string?>? settings = null) =>
        new(stubServices: false, realDatabase: true, settings: settings, configure: services =>
            services.Replace(ServiceDescriptor.Singleton<ICrisisDetector>(new MarkerCrisisDetector())));

    /// <summary>The real pipeline with the real, rule based crisis detector (the default host swaps in the marker detector).</summary>
    public ApiFactory NewFactoryWithTheRealCrisisDetector() => new(stubServices: false, realDatabase: true);

    public static string Document => "0.1-taslak";

    /// <summary>Inserts an `itest-` participant (code hash only) and remembers it for the teardown.</summary>
    public async Task<TestParticipant> CreateParticipantAsync(ParticipantStatus status = ParticipantStatus.Invited)
    {
        var code = NewCode();
        var participant = new Participant
        {
            Id = Guid.CreateVersion7(),
            CodeHash = Hasher.HashCode(code),
            Label = TestDataCleanup.NewLabel(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var db = TestDatabase.NewContext();
        db.Participants.Add(participant);
        await db.SaveChangesAsync();
        lock (_gate)
        {
            _participants.Add(participant.Id);
        }

        return new TestParticipant(participant.Id, code);
    }

    public static string NewCode()
    {
        var chars = new char[ParticipantCode.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = ParticipantCode.Alphabet[RandomNumberGenerator.GetInt32(ParticipantCode.Alphabet.Length)];
        }

        return new string(chars);
    }

    public async Task DisposeAsync()
    {
        if (Factory is null)
        {
            return;
        }

        Factory.Dispose();
        Guid[] ids;
        lock (_gate)
        {
            ids = [.. _participants];
        }

        await using var db = TestDatabase.NewContext();
        await TestDataCleanup.DeleteParticipantsAsync(db, ids);
    }
}

public sealed record TestParticipant(Guid Id, string Code);

/// <summary>A signed-in test client: bearer token plus the refresh cookie, handled by hand so rotation can be tested exactly.</summary>
public sealed class Session(HttpClient client, string accessToken, string? refreshCookie, JsonElement state)
{
    public HttpClient Client { get; } = client;

    public string AccessToken { get; } = accessToken;

    public string? RefreshCookie { get; } = refreshCookie;

    public JsonElement State { get; } = state;
}

public static class IntegrationClient
{
    public static HttpClient NewClient(this ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<(HttpResponseMessage Response, string? Cookie)> PostCodeAsync(this ApiFactory factory, string code)
    {
        var response = await factory.NewClient().PostAsync("/api/auth/code", Json($$"""{"code":"{{code}}"}"""));
        return (response, ExtractRefreshCookie(response));
    }

    public static async Task<Session> SignInAsync(this ApiFactory factory, TestParticipant participant)
    {
        var (response, cookie) = await factory.PostCodeAsync(participant.Code);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"sign-in answered {(int)response.StatusCode}; last server log lines: "
            + string.Join(" | ", factory.Logs.Rendered().TakeLast(3).Select(l => l[..Math.Min(l.Length, 300)])));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        var token = body.GetProperty("accessToken").GetString()!;
        var client = factory.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Session(client, token, cookie, body.GetProperty("state"));
    }

    public static async Task<HttpResponseMessage> RefreshAsync(this ApiFactory factory, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"lumind_refresh={cookie}");
        }

        return await factory.NewClient().SendAsync(request);
    }

    public static string? ExtractRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var header in values)
        {
            const string prefix = "lumind_refresh=";
            if (header.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = header[prefix.Length..].Split(';')[0];
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Accepts the privacy notice and the explicit consent (the onboarding the client does).</summary>
    public static async Task<JsonElement> OnboardAsync(this Session session, string nickname = "Deneme")
    {
        var consent = await session.Client.PostAsync("/api/consents", Json(
            $$"""{"records":[{"type":"privacyNotice","version":"{{IntegrationHost.Document}}","granted":true},{"type":"explicitConsent","version":"{{IntegrationHost.Document}}","granted":true}]}"""));
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        var nick = await session.Client.PutAsync("/api/me/nickname", Json($$"""{"nickname":"{{nickname}}"}"""));
        Assert.Equal(HttpStatusCode.OK, nick.StatusCode);
        return await nick.ReadJsonAsync();
    }

    public static async Task<Guid> StartConversationAsync(this Session session)
    {
        var response = await session.Client.PostAsync("/api/conversations", content: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>One parsed server-sent event.</summary>
    public sealed record SseEvent(string Name, JsonElement Data);

    public static List<SseEvent> ParseSse(string text) =>
        [.. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.StartsWith("event: ", StringComparison.Ordinal))
            .Select(block =>
            {
                var lines = block.Split('\n');
                return new SseEvent(lines[0]["event: ".Length..], JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone());
            })];

    public static async Task<(HttpResponseMessage Response, List<SseEvent> Events)> SendAsync(
        this Session session,
        Guid conversationId,
        string text,
        Guid? clientMessageId = null)
    {
        var response = await session.Client.PostAsync(
            $"/api/conversations/{conversationId}/messages",
            Json(JsonSerializer.Serialize(new { text, clientMessageId = clientMessageId ?? Guid.NewGuid() })));
        var body = await response.Content.ReadAsStringAsync();
        return (response, response.IsSuccessStatusCode ? ParseSse(body) : []);
    }

    public static string AssembleReply(this IEnumerable<SseEvent> events) =>
        string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString()));
}
