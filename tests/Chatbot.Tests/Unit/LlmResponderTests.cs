using System.Net;
using System.Text;
using System.Text.Json;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Infrastructure.Chat.Llm;
using Microsoft.Extensions.Logging;

namespace Chatbot.Tests.Unit;

/// <summary>An HttpMessageHandler that never leaves the process: it records the requests and answers from a script.</summary>
internal sealed class FakeLlmHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(Uri? Uri, HttpMethod Method, string? Authorization, string? Accept, string Body)> Requests { get; } = [];

    public int Count => Requests.Count;

    public List<CancellationToken> Tokens { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri, request.Method, request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString(), body));
        Tokens.Add(cancellationToken);
        return await respond(Requests.Count, cancellationToken);
    }

    public static HttpResponseMessage Sse(Stream content) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(content) { Headers = { ContentType = new("text/event-stream") } } };

    public static HttpResponseMessage Sse(params string[] lines) =>
        Sse(new ScriptedStream(string.Concat(lines.Select(l => l + "\n\n"))));

    public static string Data(string word) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = word } } } });

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "SECRET-BODY the provider says", string? retryAfter = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }
}

/// <summary>A response body that arrives in scripted pieces: text, a pause, a hang, or an I/O failure.</summary>
internal sealed class ScriptedStream : Stream
{
    public sealed record Pause(TimeSpan Time);

    public sealed record Hang;

    public sealed record Fail;

    private readonly Queue<object> _script = new();
    private byte[] _current = [];
    private int _offset;

    public ScriptedStream(params object[] script)
    {
        foreach (var item in script)
        {
            _script.Enqueue(item);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            if (_script.Count == 0)
            {
                return 0;
            }

            switch (_script.Dequeue())
            {
                case string text:
                    _current = Encoding.UTF8.GetBytes(text);
                    _offset = 0;
                    break;
                case Pause pause:
                    await Task.Delay(pause.Time, cancellationToken);
                    break;
                case Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                case Fail:
                    throw new IOException("SECRET-IO the connection broke");
            }
        }

        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(logLevel + ": " + formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
}

public class LlmResponderTests
{
    private const string Key = "sk-TEST-KEY-123456789";
    private const string UserText = "GIZLI-MESAJ-METNI bugün kendimi çok yorgun hissediyorum";

    private static readonly ConversationContext Context = new(
        [new ContextMessage(MessageRole.User, "ilk mesaj"), new ContextMessage(MessageRole.Assistant, "ilk yanıt"), new ContextMessage(MessageRole.User, UserText)],
        Guid.NewGuid());

    private static LlmOptions Options(Action<LlmOptions>? tweak = null)
    {
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            BaseUrl = "https://llm.example.test/v1",
            AllowedHosts = ["llm.example.test"],
            Model = "test-model-x",
            ApiKey = Key,
            RetryBaseDelayMs = 1,
            FirstTokenTimeoutSeconds = 3,
            IdleTimeoutSeconds = 3,
            TotalTimeoutSeconds = 20,
            MaxRetries = 2,
        };
        tweak?.Invoke(options);
        return options;
    }

    private static (OpenAiCompatibleChatResponder Responder, ListLogger<OpenAiCompatibleChatResponder> Log, SystemPromptText Prompt) Make(
        FakeLlmHandler handler, Action<LlmOptions>? tweak = null)
    {
        var log = new ListLogger<OpenAiCompatibleChatResponder>();
        var prompt = SystemPromptText.LoadEmbedded();
        return (new OpenAiCompatibleChatResponder(LlmHttpClient.Create(handler), Options(tweak), prompt, log, development: false), log, prompt);
    }

    private static async Task<List<string>> Read(OpenAiCompatibleChatResponder responder, CancellationToken ct = default)
    {
        var words = new List<string>();
        await foreach (var word in responder.StreamReplyAsync(Context, ct))
        {
            words.Add(word);
        }

        return words;
    }

    private static Task<HttpResponseMessage> Done(HttpResponseMessage response) => Task.FromResult(response);

    private static HttpResponseMessage Ok(params string[] words) =>
        FakeLlmHandler.Sse([.. words.Select(FakeLlmHandler.Data), "data: [DONE]"]);

    // ---------------------------------------------------------------- the request

    [Fact]
    public async Task TheRequest_GoesToChatCompletions_WithTheKeyAsBearer_AndTheModelFromTheSettings()
    {
        var handler = new FakeLlmHandler((_, _) => Done(Ok("Merhaba")));
        var (responder, _, prompt) = Make(handler);
        await Read(responder);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://llm.example.test/v1/chat/completions", request.Uri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer " + Key, request.Authorization);
        Assert.Contains("text/event-stream", request.Accept, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("test-model-x", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.6, root.GetProperty("temperature").GetDouble());
        Assert.Equal(400, root.GetProperty("max_tokens").GetInt32());
        // nothing else leaves: no user id, no metadata, no extra fields
        Assert.Equal(["max_tokens", "messages", "model", "stream", "temperature"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());

        var messages = root.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString())).ToList();
        Assert.Equal(4, messages.Count);
        Assert.Equal(("system", prompt.Text), messages[0]);
        Assert.Equal(("user", "ilk mesaj"), messages[1]);
        Assert.Equal(("assistant", "ilk yanıt"), messages[2]);
        Assert.Equal(("user", UserText), messages[3]);
        Assert.Equal(prompt.Version, responder.PromptVersion);
    }

    [Fact]
    public async Task TheModelName_IsOnlyWhatTheSettingsSay_AndTheTokenFieldNameFollowsTheSettings()
    {
        var handler = new FakeLlmHandler((_, _) => Done(Ok("a")));
        var (responder, _, _) = Make(handler, o =>
        {
            o.Model = "another/model:7b";
            o.MaxTokensParameter = "max_completion_tokens";
            o.Temperature = null;
        });
        await Read(responder);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("another/model:7b", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void TheHttpClient_FollowsNoRedirectAndKeepsNoCookies_AndHasNoTimeoutOfItsOwn()
    {
        var handler = LlmHttpClient.CreateDefaultHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(Timeout.InfiniteTimeSpan, LlmHttpClient.Create().Timeout);
    }

    [Fact]
    public async Task ARedirect_IsARefusal_NotSomethingToFollowOrRepeat()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Status(HttpStatusCode.TemporaryRedirect)));
        var (responder, _, _) = Make(handler);

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.Rejected, ex.Failure);
        Assert.Equal(1, handler.Count);
    }

    // ---------------------------------------------------------------- the stream

    [Fact]
    public async Task TheStream_IsParsedWordByWord_SkippingCommentsRoleOnlyAndEmptyEvents_UntilDone()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(
            ": keep-alive",
            "event: message\ndata: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { role = "assistant" } } } }),
            FakeLlmHandler.Data("Merhaba"),
            FakeLlmHandler.Data(string.Empty),
            FakeLlmHandler.Data(" dünya"),
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } }, usage = new { completion_tokens = 3 } }),
            "data: [DONE]",
            FakeLlmHandler.Data("ASLA-GORUNMEMELI"))));
        var (responder, _, _) = Make(handler);

        var words = await Read(responder);

        Assert.Equal(["Merhaba", " dünya"], words);
    }

    [Fact]
    public async Task AnEventThatIsCutInTheMiddleOfAWord_IsPutTogether_AndATurkishLetterSplitAcrossBytesSurvives()
    {
        var line = FakeLlmHandler.Data("Güzel şeyler") + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1; // between the two bytes of "ü"
        var halves = new HalfStream(bytes, cut);
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(halves)));
        var (responder, _, _) = Make(handler);

        Assert.Equal(["Güzel şeyler"], await Read(responder));
    }

    private sealed class HalfStream(byte[] all, int cut) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => all.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= all.Length)
            {
                return ValueTask.FromResult(0);
            }

            var limit = _position < cut ? cut : all.Length; // the first read ends in the middle of a character
            var n = Math.Min(buffer.Length, limit - _position);
            all.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return ValueTask.FromResult(n);
        }
    }

    [Fact]
    public async Task AnEmptyReply_IsRetried_ThenFailsAsABadAnswer()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse("data: [DONE]")));
        var (responder, _, _) = Make(handler);

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.BadAnswer, ex.Failure);
        Assert.Equal(3, handler.Count); // 1 + MaxRetries
    }

    [Fact]
    public async Task AnErrorEventInsideTheStream_IsABadAnswer_BeforeTheFirstWordItIsRetried()
    {
        var handler = new FakeLlmHandler((n, _) => Done(n == 1
            ? FakeLlmHandler.Sse("data: " + JsonSerializer.Serialize(new { error = new { message = "SECRET-EVENT-ERROR" } }))
            : Ok("tamam")));
        var (responder, _, _) = Make(handler);

        Assert.Equal(["tamam"], await Read(responder));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task AMalformedEventAfterTheFirstWord_EndsTheReplyAsABadAnswer_WithoutRepeating()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(FakeLlmHandler.Data("Merhaba"), "data: {not json")));
        var (responder, _, _) = Make(handler);

        var words = new List<string>();
        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var w in responder.StreamReplyAsync(Context, default))
            {
                words.Add(w);
            }
        });
        Assert.Equal(["Merhaba"], words);
        Assert.Equal(ModelFailure.BadAnswer, ex.Failure);
        Assert.Equal(1, handler.Count);
    }

    // ---------------------------------------------------------------- retries: before the first word only

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)429)]
    public async Task ARetryableStatus_IsRepeated_AndTheSecondAnswerIsUsed(HttpStatusCode first)
    {
        var handler = new FakeLlmHandler((n, _) => Done(n == 1 ? FakeLlmHandler.Status(first, retryAfter: "0") : Ok("Merhaba")));
        var (responder, log, _) = Make(handler);

        Assert.Equal(["Merhaba"], await Read(responder));
        Assert.Equal(2, handler.Count);
        Assert.Contains(log.Lines, l => l.Contains("attempt 1 failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANetworkFailure_IsRepeated()
    {
        var handler = new FakeLlmHandler((n, _) => n == 1 ? throw new HttpRequestException("SECRET-HTTP-EX connection refused") : Done(Ok("Merhaba")));
        var (responder, _, _) = Make(handler);

        Assert.Equal(["Merhaba"], await Read(responder));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task WhenEveryAttemptFails_ItStopsAfterMaxRetries_WithTheLastCategory()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Status(HttpStatusCode.ServiceUnavailable)));
        var (responder, _, _) = Make(handler, o => o.MaxRetries = 2);

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.ServerError, ex.Failure);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(3, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AConfigurationProblem_IsNotRepeated(HttpStatusCode status)
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Status(status)));
        var (responder, log, _) = Make(handler);

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.Rejected, ex.Failure);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Equal(1, handler.Count);
        Assert.Contains(log.Lines, l => l.StartsWith("Error:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFirstWordThatDoesNotComeInTime_IsATimeout_AndIsRepeatedOnce_WhenMaxRetriesIsOne()
    {
        var handler = new FakeLlmHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeLlmHandler.Status(HttpStatusCode.OK);
        });
        var (responder, _, _) = Make(handler, o =>
        {
            o.FirstTokenTimeoutSeconds = 0.05;
            o.MaxRetries = 1;
        });

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.Timeout, ex.Failure);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task AStreamThatOpensButNeverSendsAWord_IsAFirstWordTimeout()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream(": hello\n\n", new ScriptedStream.Hang()))));
        var (responder, _, _) = Make(handler, o =>
        {
            o.FirstTokenTimeoutSeconds = 0.1;
            o.MaxRetries = 0;
        });

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task OnceAWordWasShown_NothingIsRepeated_ABrokenConnectionEndsTheReply()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream(FakeLlmHandler.Data("Merhaba") + "\n\n", new ScriptedStream.Fail()))));
        var (responder, _, _) = Make(handler);

        var words = new List<string>();
        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var w in responder.StreamReplyAsync(Context, default))
            {
                words.Add(w);
            }
        });

        Assert.Equal(["Merhaba"], words);
        Assert.Equal(ModelFailure.Network, ex.Failure);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ASilenceAfterTheFirstWord_IsAnIdleTimeout_AndNothingIsRepeated()
    {
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream(FakeLlmHandler.Data("Merhaba") + "\n\n", new ScriptedStream.Hang()))));
        var (responder, _, _) = Make(handler, o => o.IdleTimeoutSeconds = 0.1);

        var words = new List<string>();
        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var w in responder.StreamReplyAsync(Context, default))
            {
                words.Add(w);
            }
        });

        Assert.Equal(["Merhaba"], words);
        Assert.Equal(ModelFailure.Timeout, ex.Failure);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task AReplyThatTrickles_IsStoppedByTheTotalLimit()
    {
        var script = new List<object>();
        for (var i = 0; i < 100; i++)
        {
            script.Add(new ScriptedStream.Pause(TimeSpan.FromMilliseconds(30)));
            script.Add(FakeLlmHandler.Data("w") + "\n\n");
        }

        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream([.. script]))));
        var (responder, _, _) = Make(handler, o => o.TotalTimeoutSeconds = 0.25);

        var words = new List<string>();
        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(async () =>
        {
            await foreach (var w in responder.StreamReplyAsync(Context, default))
            {
                words.Add(w);
            }
        });

        Assert.Equal(ModelFailure.Timeout, ex.Failure);
        Assert.InRange(words.Count, 1, 40);
    }

    // ---------------------------------------------------------------- cancellation and load

    [Fact]
    public async Task WhenTheClientGoesAway_TheRequestToTheProviderIsCancelledToo_AndItIsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream(FakeLlmHandler.Data("Merhaba") + "\n\n", new ScriptedStream.Hang()))));
        var (responder, _, _) = Make(handler);

        var words = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var w in responder.StreamReplyAsync(Context, cts.Token))
            {
                words.Add(w);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["Merhaba"], words);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task OnlyMaxConcurrentRequestsRunAtOnce_TheNextOneIsBusy_AndTheSlotIsGivenBack()
    {
        var hold = new TaskCompletionSource();
        var handler = new FakeLlmHandler((_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream(FakeLlmHandler.Data("a") + "\n\n", new ScriptedStream.Hang()))));
        var (responder, _, _) = Make(handler, o =>
        {
            o.MaxConcurrentRequests = 1;
            o.FirstTokenTimeoutSeconds = 0.1;
        });

        using var firstCts = new CancellationTokenSource();
        await using var first = responder.StreamReplyAsync(Context, firstCts.Token).GetAsyncEnumerator(firstCts.Token);
        Assert.True(await first.MoveNextAsync()); // the slot is taken while this reply is open
        hold.SetResult();

        var busy = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));
        Assert.Equal(ModelFailure.Busy, busy.Failure);
        Assert.Equal(1, handler.Count);

        await first.DisposeAsync(); // the slot comes back
        var again = new FakeLlmHandler((_, _) => Done(Ok("x")));
        var (second, _, _) = Make(again, o => o.MaxConcurrentRequests = 1);
        Assert.Equal(["x"], await Read(second));
    }

    // ---------------------------------------------------------------- nothing leaks

    [Fact]
    public async Task NoFailure_CarriesAResponseBody_AMessageText_ThePrompt_OrTheKey_NotInTheException_NotInTheLog()
    {
        var failures = new Func<int, CancellationToken, Task<HttpResponseMessage>>[]
        {
            (_, _) => Done(FakeLlmHandler.Status(HttpStatusCode.InternalServerError, "SECRET-BODY " + UserText + " " + Key)),
            (_, _) => Done(FakeLlmHandler.Status(HttpStatusCode.Unauthorized, "SECRET-BODY invalid key " + Key)),
            (_, _) => throw new HttpRequestException("SECRET-HTTP-EX " + Key + " " + UserText),
            (_, _) => Done(FakeLlmHandler.Sse("data: " + JsonSerializer.Serialize(new { error = new { message = "SECRET-BODY " + UserText } }))),
            (_, _) => Done(FakeLlmHandler.Sse(FakeLlmHandler.Data("ok") , "data: {SECRET-BODY broken " + UserText)),
            (_, _) => Done(FakeLlmHandler.Sse(new ScriptedStream(": x\n\n", new ScriptedStream.Fail()))),
        };

        foreach (var respond in failures)
        {
            var handler = new FakeLlmHandler(respond);
            var (responder, log, prompt) = Make(handler);

            var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));

            var everything = ex + " | " + ex.Message + " | " + string.Join(" | ", log.Lines);
            foreach (var secret in new[] { "SECRET", UserText, Key, "GIZLI-MESAJ", prompt.Text[..40], "llm.example.test" })
            {
                Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ASuccessfulReply_LogsOnlyCountsAndDurations()
    {
        var handler = new FakeLlmHandler((_, _) => Done(Ok("Merhaba", " dünya")));
        var (responder, log, prompt) = Make(handler);
        await Read(responder);

        var text = string.Join(" | ", log.Lines);
        Assert.Contains("Model reply finished", text, StringComparison.Ordinal);
        foreach (var secret in new[] { "Merhaba", "dünya", UserText, Key, prompt.Text[..40] })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the allow list at construction

    [Theory]
    [InlineData("https://evil.example.test/v1")]
    [InlineData("http://llm.example.test/v1")]
    [InlineData("https://user:pass@llm.example.test/v1")]
    [InlineData("not a url")]
    public void ABaseUrlThatIsNotAllowed_StopsTheResponderFromBeingBuilt_WithoutNamingTheValue(string baseUrl)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OpenAiCompatibleChatResponder(LlmHttpClient.Create(new FakeLlmHandler((_, _) => throw new InvalidOperationException())), Options(o => o.BaseUrl = baseUrl),
                SystemPromptText.LoadEmbedded(), new ListLogger<OpenAiCompatibleChatResponder>(), development: false));

        Assert.StartsWith("Invalid Llm configuration:", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example.test", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pass", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
    }
}
