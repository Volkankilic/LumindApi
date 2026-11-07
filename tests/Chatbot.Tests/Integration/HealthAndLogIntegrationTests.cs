using System.Net;
using Chatbot.Tests.TestSupport;

namespace Chatbot.Tests.Integration;

public class HealthAndLogIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    [PostgresFact]
    public async Task TheStartupSchemaCheck_Passes_AgainstTheRealDatabase()
    {
        var response = await host.Factory.NewClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("schema", out _), "outside Development the answer is ready / not ready and nothing more");
    }

    [PostgresFact]
    public async Task InDevelopment_TheHealthAnswer_ShowsTheSchemaVersion()
    {
        using var development = new ApiFactory(
            environment: "Development",
            settings: new() { ["ConnectionStrings:Default"] = TestDatabase.ConnectionString },
            stubServices: false);
        var response = await development.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var schema = (await response.ReadJsonAsync()).GetProperty("schema");
        Assert.Equal(new Chatbot.Application.Options.DatabaseOptions().ExpectedSchemaVersion, schema.GetProperty("expected").GetInt32());
        Assert.True(schema.GetProperty("found").GetInt32() >= 1);
    }

    [PostgresFact]
    public async Task WhenTheDatabaseIsBehindTheBuild_HealthIsNotReady_AndProductionSaysNothingMore()
    {
        using var ahead = host.NewFactory(new() { ["Database:ExpectedSchemaVersion"] = "99" });
        var response = await ahead.NewClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("not-ready", text, StringComparison.Ordinal);
        Assert.DoesNotContain("99", text, StringComparison.Ordinal);
        Assert.DoesNotContain("api/db", text, StringComparison.Ordinal);
        // The operator still learns what to do, from the log.
        // The startup check runs in the background against the remote database, so the line can arrive after the health answer.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !ahead.Logs.Rendered().Any(line => line.Contains("Apply the scripts in api/db/", StringComparison.Ordinal)))
        {
            await Task.Delay(100);
        }

        Assert.Contains(ahead.Logs.Rendered(), line => line.Contains("Apply the scripts in api/db/", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task TheWholeFlow_LeavesNoContentCodeNicknameOrTokenInTheLogs()
    {
        const string text = "GIZLI-MESAJ-METNI-77 bugün kendimi yorgun hissediyorum";
        const string nickname = "GizliTakmaAd";
        using var factory = host.NewFactory();
        var participant = await host.CreateParticipantAsync();

        var (codeResponse, cookie) = await factory.PostCodeAsync(participant.Code);
        var token = (await codeResponse.ReadJsonAsync()).GetProperty("accessToken").GetString()!;
        var session = await factory.SignInAsync(participant); // a second sign-in: also exercises the revoke path
        await session.OnboardAsync(nickname);
        var conversation = await session.StartConversationAsync();
        var (_, events) = await session.SendAsync(conversation, text);
        await session.Client.GetAsync($"/api/conversations/{conversation}");
        await factory.RefreshAsync(session.RefreshCookie);
        await factory.RefreshAsync(session.RefreshCookie); // reuse -> warning path
        await factory.PostCodeAsync(IntegrationHost.NewCode()); // failed sign-in
        await session.Client.GetAsync($"/api/conversations/{Guid.NewGuid()}"); // 404 path
        await session.Client.DeleteAsync("/api/me");

        var logs = string.Join("\n", factory.Logs.Rendered());
        Assert.Contains("responded", logs, StringComparison.Ordinal); // the request log really was captured
        var markers = new Dictionary<string, string?>
        {
            ["message text"] = "GIZLI-MESAJ-METNI",
            ["reply text"] = events.AssembleReply().Split(' ').Take(6).Aggregate((a, b) => $"{a} {b}"),
            ["nickname"] = nickname,
            ["participant code"] = participant.Code,
            ["refresh cookie"] = cookie,
            ["access token"] = token,
            ["session access token"] = session.AccessToken,
            ["participant id"] = participant.Id.ToString(),
            ["conversation id"] = conversation.ToString(),
            ["client message id"] = events[0].Data.GetProperty("userMessageId").GetString(),
            ["bearer scheme"] = "Bearer ",
        };
        foreach (var (name, value) in markers)
        {
            Assert.False(string.IsNullOrEmpty(value), $"the {name} marker is empty: the test would prove nothing");
            Assert.DoesNotContain(value!, logs, StringComparison.Ordinal);
        }
    }
}
