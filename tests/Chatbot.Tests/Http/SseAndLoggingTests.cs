using System.Net;
using System.Text;
using Chatbot.Application;
using Chatbot.Application.Chat;
using Chatbot.Tests.TestSupport;

namespace Chatbot.Tests.Http;

public class SseEndpointTests
{
    private static string Url => $"/api/conversations/{HttpHelpers.SomeConversation}/messages";

    private static StringContent Body(string text = "merhaba", Guid? clientId = null) =>
        HttpHelpers.Json($$"""{"text":"{{text}}","clientMessageId":"{{clientId ?? Guid.NewGuid()}}"}""");

    private static readonly Guid UserMessage = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AssistantMessage = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public async Task TheStream_HasTheContractHeaders_AndTheEventFormat_InOrder()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => StubChatService.Events(
            new ChatEvent.Accepted(UserMessage),
            new ChatEvent.Delta("Merhaba "),
            new ChatEvent.Delta("\"dünya\"\nyeni satır"),
            new ChatEvent.Done(AssistantMessage));

        var response = await factory.Authed().PostAsync(Url, Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            $"event: accepted\ndata: {{\"userMessageId\":\"{UserMessage}\"}}\n\n" +
            "event: delta\ndata: {\"text\":\"Merhaba \"}\n\n" +
            "event: delta\ndata: {\"text\":\"\\u0022d\\u00FCnya\\u0022\\nyeni sat\\u0131r\"}\n\n" +
            $"event: done\ndata: {{\"assistantMessageId\":\"{AssistantMessage}\"}}\n\n",
            text.Replace("\\\"", "\\u0022").Replace("ü", "\\u00FC").Replace("ı", "\\u0131"));
        Assert.All(text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries), block =>
        {
            var lines = block.Split('\n');
            Assert.Equal(2, lines.Length); // `event:` and ONE `data:` line, even when the text holds a newline
            Assert.StartsWith("event: ", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("data: ", lines[1], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ACrisisStream_HasTheCrisisEventBetweenAcceptedAndTheFirstDelta()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => StubChatService.Events(
            new ChatEvent.Accepted(UserMessage), new ChatEvent.Crisis(), new ChatEvent.Delta("x"), new ChatEvent.Done(AssistantMessage));
        var text = await (await factory.Authed().PostAsync(Url, Body())).Content.ReadAsStringAsync();

        var names = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split('\n')[0]).ToArray();
        Assert.Equal(["event: accepted", "event: crisis", "event: delta", "event: done"], names);
        Assert.Contains("event: crisis\ndata: {}\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorAfterAccepted_IsAnEventInsideTheStream_TheStatusStays200()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => StubChatService.Events(
            new ChatEvent.Accepted(UserMessage), new ChatEvent.Delta("Merhaba "), new ChatEvent.Error("unavailable"));
        var response = await factory.Authed().PostAsync(Url, Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("event: error\ndata: {\"code\":\"unavailable\"}\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitedErrorEvent_CarriesRetryAfterSeconds()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => StubChatService.Events(new ChatEvent.Accepted(UserMessage), new ChatEvent.Error("rate-limited", 20));
        var text = await (await factory.Authed().PostAsync(Url, Body())).Content.ReadAsStringAsync();
        Assert.Contains("data: {\"code\":\"rate-limited\",\"retryAfterSeconds\":20}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureThrownAfterAccepted_BecomesAnErrorEvent_WithoutDetail()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => Failing(new InvalidOperationException("secret detail"));
        var response = await factory.Authed().PostAsync(Url, Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("event: error\ndata: {\"code\":\"unknown\"}\n\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);

        static async IAsyncEnumerable<ChatEvent> Failing(Exception ex)
        {
            yield return new ChatEvent.Accepted(UserMessage);
            await Task.CompletedTask;
            throw ex;
        }
    }

    [Theory]
    [InlineData(422, "too-long")]
    [InlineData(422, "empty-message")]
    [InlineData(409, "conversation-ended")]
    [InlineData(404, "not-found")]
    [InlineData(503, "unavailable")]
    public async Task AFailureBeforeAccepted_IsAPlainHttpError_NotAnEventStream(int status, string code)
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => Throwing(new AppException(code, status));
        var response = await factory.Authed().PostAsync(Url, Body());

        await response.AssertProblem((HttpStatusCode)status, code);
        Assert.DoesNotContain("event-stream", response.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.Ordinal);

        static async IAsyncEnumerable<ChatEvent> Throwing(Exception ex)
        {
            await Task.CompletedTask;
            throw ex;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task ARateLimitBeforeAccepted_Is429WithRetryAfterHeader()
    {
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => Throwing();
        var response = await factory.Authed().PostAsync(Url, Body());

        await response.AssertProblem(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.Equal(20, response.Headers.RetryAfter?.Delta?.TotalSeconds);

        static async IAsyncEnumerable<ChatEvent> Throwing()
        {
            await Task.CompletedTask;
            throw AppException.RateLimited(20);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task AMissingClientMessageId_Is422()
    {
        using var factory = new ApiFactory();
        var response = await factory.Authed().PostAsync(Url, HttpHelpers.Json("""{"text":"merhaba"}"""));
        await response.AssertProblem(HttpStatusCode.UnprocessableEntity, "validation-failed");
    }

    [Fact]
    public async Task TheStream_ArrivesPieceByPiece_NotAllAtTheEnd()
    {
        using var factory = new ApiFactory();
        var gate = new TaskCompletionSource();
        factory.Chat.Handler = _ => Slow(gate.Task);
        var client = factory.Authed();

        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = Body() };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // The first event is readable while the server is still waiting for the rest.
        Assert.Equal("event: accepted", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.StartsWith("data: ", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)), StringComparison.Ordinal);
        Assert.Equal(string.Empty, await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.Task.IsCompleted, "the server has not finished: the first event came through a flush");

        gate.SetResult();
        Assert.Equal("event: delta", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        static async IAsyncEnumerable<ChatEvent> Slow(Task gate)
        {
            yield return new ChatEvent.Accepted(UserMessage);
            await gate;
            yield return new ChatEvent.Delta("geç geldi");
            yield return new ChatEvent.Done(AssistantMessage);
        }
    }

    [Fact]
    public async Task SendingMessages_IsRateLimitedPerParticipant()
    {
        using var factory = new ApiFactory(settings: new() { ["Chat:MessagesPerMinute"] = "2" });
        factory.Chat.Handler = _ => StubChatService.Events(new ChatEvent.Accepted(UserMessage), new ChatEvent.Done(AssistantMessage));
        var client = factory.Authed();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url, Body())).StatusCode);
        var limited = await client.PostAsync(Url, Body());
        await limited.AssertProblem(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.NotNull(limited.Headers.RetryAfter);

        // another participant is not affected
        var other = factory.Authed(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync(Url, Body())).StatusCode);
    }
}

public class LoggingAndConfigurationTests
{
    [Fact]
    public async Task TheRequestLog_HasMethodRouteTemplateStatusAndDuration_Only()
    {
        const string secretQuery = "SECRETQUERY-9f3a";
        const string secretText = "çok-özel-mesaj-metni";
        using var factory = new ApiFactory();
        factory.Chat.Handler = _ => StubChatService.Events(new ChatEvent.Accepted(Guid.NewGuid()), new ChatEvent.Done(Guid.NewGuid()));
        var conversation = Guid.NewGuid();
        var client = factory.Authed();
        client.DefaultRequestHeaders.Add("Cookie", "lumind_refresh=COOKIEVALUE-77");

        await client.PostAsync(
            $"/api/conversations/{conversation}/messages?token={secretQuery}",
            HttpHelpers.Json($$"""{"text":"{{secretText}}","clientMessageId":"{{Guid.NewGuid()}}"}"""));
        await client.GetAsync($"/api/conversations/{conversation}");

        var logs = string.Join("\n", factory.Logs.Rendered());
        Assert.Contains("api/conversations/{id:guid}/messages", logs, StringComparison.Ordinal); // the route TEMPLATE
        Assert.Contains("responded 200", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secretQuery, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("COOKIEVALUE", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(conversation.ToString(), logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Services.GetService(typeof(Chatbot.Application.Abstractions.IAccessTokenIssuer)) is null ? "x" : "eyJ", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeakChecks_ReallyBite_AMarkerInALogEventIsFound()
    {
        // Self test: prove that the assertions above would fail if something leaked.
        var sink = new CapturingSink();
        sink.Emit(new Serilog.Events.LogEvent(
            DateTimeOffset.UtcNow,
            Serilog.Events.LogEventLevel.Information,
            null,
            new Serilog.Parsing.MessageTemplateParser().Parse("path {Path}"),
            [new Serilog.Events.LogEventProperty("Path", new Serilog.Events.ScalarValue("/x?token=SECRETQUERY-9f3a"))]));
        Assert.Contains("SECRETQUERY", string.Join("\n", sink.Rendered()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSecrets_StopTheApplication_WithAMessageThatNamesTheSettingNotItsValue()
    {
        const string tooShort = "SHORT-PEPPER-VALUE";
        using var factory = new ApiFactory(settings: new()
        {
            ["Auth:CodePepper"] = tooShort,
            ["Auth:JwtSigningKey"] = null,
        });

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/api/health");
        });

        var message = Flatten(ex);
        Assert.Contains("Auth:CodePepper", message, StringComparison.Ordinal);
        Assert.Contains("Auth:JwtSigningKey", message, StringComparison.Ordinal);
        Assert.DoesNotContain(tooShort, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorsWildcard_StopsTheApplication()
    {
        using var factory = new ApiFactory(settings: new() { ["Cors:AllowedOrigins:0"] = "*" });
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/api/health");
        });
        Assert.Contains("Cors:AllowedOrigins", Flatten(ex), StringComparison.Ordinal);
    }

    private static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            sb.AppendLine(e.Message);
            if (e.InnerException is null)
            {
                break;
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Cors_AllowsOnlyTheConfiguredOrigins_WithCredentials_AndNoWildcard()
    {
        using var factory = new ApiFactory(settings: new() { ["Cors:AllowedOrigins:0"] = "https://app.example.org" });
        var client = factory.CreateClient();

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        allowed.Headers.Add("Origin", "https://app.example.org");
        var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal("https://app.example.org", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowedResponse.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var other = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        other.Headers.Add("Origin", "https://evil.example.com");
        Assert.False((await client.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task WithNoConfiguredOrigin_NoCrossOriginCallIsAllowed()
    {
        using var factory = new ApiFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://app.example.org");
        Assert.False((await factory.CreateClient().SendAsync(request)).Headers.Contains("Access-Control-Allow-Origin"));
    }
}
