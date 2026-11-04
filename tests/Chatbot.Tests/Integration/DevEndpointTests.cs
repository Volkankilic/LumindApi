using System.Net;
using System.Net.Http.Headers;
using Chatbot.Infrastructure.Dev;
using Chatbot.Tests.Http;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Integration;

/// <summary>The development-only test participant endpoints: they exist in Development and nowhere else.</summary>
public class DevEndpointAvailabilityTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public async Task OutsideDevelopment_TheServiceIsNotRegistered_AndTheRoutesDoNotExist(string environment)
    {
        using var factory = new ApiFactory(environment);
        using var scope = factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<DevTestParticipantService>());

        var client = factory.CreateClient();
        var create = await client.PostAsync("/api/dev/test-participants", HttpHelpers.Json("""{"state":"ready"}"""));
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        var delete = await client.DeleteAsync($"/api/dev/test-participants/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public void InDevelopment_TheServiceIsRegistered()
    {
        using var factory = new ApiFactory("Development");
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<DevTestParticipantService>());
    }

    [Theory]
    [InlineData("itest-abc", true)]
    [InlineData("itest-e2e-0a1b", true)]
    [InlineData("seed-1", false)]
    [InlineData("itest-", false)]
    [InlineData("ITEST-abc", false)]
    [InlineData("xitest-abc", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyItestLabelsCountAsTestParticipants(string? label, bool expected) =>
        Assert.Equal(expected, DevTestParticipantService.IsTestLabel(label));

    [Fact]
    public void GeneratedLabels_AlwaysCarryTheTestPrefix() =>
        Assert.StartsWith("itest-e2e-", DevTestParticipantService.NewLabel(), StringComparison.Ordinal);
}

public class DevEndpointDatabaseTests : IAsyncLifetime
{
    private readonly List<Guid> _created = [];
    private ApiFactory? _factory;

    public Task InitializeAsync()
    {
        if (TestDatabase.EnsureAvailable())
        {
            _factory = new ApiFactory(
                "Development",
                new() { ["ConnectionStrings:Default"] = TestDatabase.ConnectionString, ["Auth:CodeAttempts"] = "10000" },
                stubServices: false);
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        _factory.Dispose();
        await using var db = TestDatabase.NewContext();
        await TestDataCleanup.DeleteParticipantsAsync(db, _created); // safety net: tests normally delete through the endpoint
    }

    private async Task<(Guid Id, string Code)> CreateAsync(string state)
    {
        var response = await _factory!.CreateClient().PostAsync("/api/dev/test-participants", HttpHelpers.Json($$"""{"state":"{{state}}"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Body();
        var id = body.GetProperty("participantId").GetGuid();
        _created.Add(id);
        return (id, body.GetProperty("code").GetString()!);
    }

    private async Task<HttpClient> SignInAsync(string code)
    {
        var response = await _factory!.CreateClient().PostAsync("/api/auth/code", HttpHelpers.Json($$"""{"code":"{{code}}"}"""));
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"sign-in failed with {(int)response.StatusCode}");
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await response.Body()).GetProperty("accessToken").GetString());
        return client;
    }

    [PostgresFact]
    public async Task New_IsInvited_WithoutConsent()
    {
        var (id, code) = await CreateAsync("new");
        var client = await SignInAsync(code);
        var state = await (await client.GetAsync("/api/me")).Body();

        Assert.Equal(id, state.GetProperty("participantId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, state.GetProperty("noticeAcknowledgedVersion").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/conversations", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Ready_IsFullyOnboarded_AndCanChat()
    {
        var (_, code) = await CreateAsync("ready");
        var client = await SignInAsync(code);
        var state = await (await client.GetAsync("/api/me")).Body();

        Assert.Equal("Deneme", state.GetProperty("nickname").GetString());
        Assert.True(state.GetProperty("consent").GetProperty("processing").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/conversations", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Withdrawn_HasNoConsent_AndNoNickname()
    {
        var (_, code) = await CreateAsync("withdrawn");
        var client = await SignInAsync(code);
        var state = await (await client.GetAsync("/api/me")).Body();

        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, state.GetProperty("noticeAcknowledgedVersion").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, state.GetProperty("consent").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/conversations", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Revoked_CannotSignIn()
    {
        var (_, code) = await CreateAsync("revoked");
        var response = await _factory!.CreateClient().PostAsync("/api/auth/code", HttpHelpers.Json($$"""{"code":"{{code}}"}"""));
        await response.AssertProblem(HttpStatusCode.Unauthorized, "invalid-code");
    }

    [PostgresFact]
    public async Task Outdated_HasAnOldDocumentVersion_SoConsentIsAskedAgain()
    {
        var (_, code) = await CreateAsync("outdated");
        var client = await SignInAsync(code);
        var state = await (await client.GetAsync("/api/me")).Body();

        Assert.Equal("0.0-eski", state.GetProperty("noticeAcknowledgedVersion").GetString());
        Assert.Equal("0.0-eski", state.GetProperty("consent").GetProperty("version").GetString());
        var blocked = await client.PostAsync("/api/conversations", null);
        await blocked.AssertProblem(HttpStatusCode.Forbidden, "consent-required");
    }

    [PostgresFact]
    public async Task AnUnknownState_Is422()
    {
        var response = await _factory!.CreateClient().PostAsync("/api/dev/test-participants", HttpHelpers.Json("""{"state":"admin"}"""));
        await response.AssertProblem(HttpStatusCode.UnprocessableEntity, "validation-failed");
    }

    [PostgresFact]
    public async Task Delete_RemovesTheParticipantAndEverythingThatBelongsToIt()
    {
        var (id, code) = await CreateAsync("ready");
        var client = await SignInAsync(code);
        var conversation = (await (await client.PostAsync("/api/conversations", null)).Body()).GetProperty("id").GetGuid();
        var send = await client.PostAsync(
            $"/api/conversations/{conversation}/messages",
            HttpHelpers.Json($$"""{"text":"merhaba","clientMessageId":"{{Guid.NewGuid()}}"}"""));
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        await send.Content.ReadAsStringAsync();

        var delete = await _factory!.CreateClient().DeleteAsync($"/api/dev/test-participants/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using var db = TestDatabase.NewContext();
        Assert.False(await db.Participants.AnyAsync(p => p.Id == id));
        Assert.False(await db.Conversations.AnyAsync(c => c.ParticipantId == id));
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == conversation));
        Assert.False(await db.ConsentRecords.AnyAsync(r => r.ParticipantId == id));
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.ParticipantId == id));
        Assert.False(await db.AuditLogs.AnyAsync(a => a.ActorId == id));

        var again = await _factory.CreateClient().DeleteAsync($"/api/dev/test-participants/{id}");
        await again.AssertProblem(HttpStatusCode.NotFound, "not-found");
    }

    [PostgresFact]
    public async Task Delete_RefusesParticipantsThatAreNotTestParticipants_SeedIncluded()
    {
        await using var db = TestDatabase.NewContext();
        var seed = await db.Participants.AsNoTracking().Where(p => p.Label != null && p.Label.StartsWith("seed-")).Select(p => p.Id).ToListAsync();
        Assert.NotEmpty(seed);

        foreach (var id in seed)
        {
            var response = await _factory!.CreateClient().DeleteAsync($"/api/dev/test-participants/{id}");
            await response.AssertProblem(HttpStatusCode.Forbidden, "forbidden");
        }

        Assert.Equal(seed.Count, await db.Participants.CountAsync(p => p.Label != null && p.Label.StartsWith("seed-")));
    }
}
