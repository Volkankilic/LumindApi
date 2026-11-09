using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Chat.Llm;

/// <summary>
/// Streams a reply from any OpenAI compatible <c>/chat/completions</c> endpoint (a provider hosted in Turkey, vLLM, ...). Plain
/// <see cref="HttpClient"/> and a small SSE reader: no provider SDK.
///
/// What it guarantees, and the tests pin each of it:
/// - the base URL passed the allow list (<see cref="LlmEndpointPolicy"/>); redirects are never followed;
/// - what leaves is the system prompt and the already MASKED context, nothing else (no ids, no nickname, no time);
/// - a failure BEFORE the first word is retried (bounded, with a pause); once a word was shown nothing is repeated;
/// - a failure surfaces only as <see cref="ModelUnavailableException"/> with a fixed category: never a response body, never a message
///   text, never the key, and the logs say only what happened (attempt, category, status code, durations).
/// </summary>
public sealed class OpenAiCompatibleChatResponder : IChatResponder
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly ISystemPromptProvider _prompt;
    private readonly ILogger<OpenAiCompatibleChatResponder> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, JsonElement> _extra;

    public OpenAiCompatibleChatResponder(
        HttpClient http,
        LlmOptions options,
        ISystemPromptProvider prompt,
        ILogger<OpenAiCompatibleChatResponder> logger,
        bool development)
    {
        var problems = OptionsValidator.ValidateEndpoint(options, development);
        if (problems.Count > 0)
        {
            // The messages name settings only, never values.
            throw new InvalidOperationException("Invalid Llm configuration: " + string.Join(" ", problems));
        }

        _http = http;
        _options = options;
        _prompt = prompt;
        _logger = logger;
        _gate = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
        _endpoint = new Uri(options.BaseUrl!.Trim().TrimEnd('/') + "/chat/completions");
        _extra = LlmExtraBody.Parse(options.ExtraBodyJson, options.MaxTokensParameter, "Llm:ExtraBodyJson").Fields; // validated above
    }

    public string? PromptVersion => _prompt.Version;

    /// <summary>The NAMES of the extra body fields that are sent (never their values).</summary>
    public IReadOnlyList<string> ExtraBodyKeys => [.. _extra.Keys];

    public IAsyncEnumerable<string> StreamReplyAsync(ConversationContext context, CancellationToken ct) =>
        StreamAsync(context, new ReplyStats(), ct);

    /// <summary>
    /// The reply as words, like <see cref="StreamReplyAsync"/>, and fills <paramref name="stats"/> on the way (token count the server reports,
    /// characters of reasoning it streamed). Only the "content" of a reply is ever yielded: the reasoning is counted and dropped.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(ConversationContext context, ReplyStats stats, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        if (!await _gate.WaitAsync(Seconds(_options.FirstTokenTimeoutSeconds), ct))
        {
            _logger.LogWarning("Model request not sent: {Limit} requests are already running.", _options.MaxConcurrentRequests);
            throw new ModelUnavailableException(ModelFailure.Busy);
        }

        try
        {
            var body = BuildBody(context);
            await using var reply = await StartAsync(body, started, stats, ct);
            var parts = 1;
            yield return reply.First;
            while (await reply.NextAsync(ct) is { } next)
            {
                parts++;
                yield return next;
            }

            _logger.LogInformation(
                "Model reply finished: {Parts} parts, first part after {FirstMs} ms, {TotalMs} ms in total, {Attempts} attempt(s).",
                parts,
                (long)reply.FirstAfter.TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                reply.Attempts);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- the request

    private string BuildBody(ConversationContext context)
    {
        var messages = new List<object> { new { role = "system", content = _prompt.Text } };
        foreach (var message in context.Messages)
        {
            messages.Add(new { role = message.Role == MessageRole.User ? "user" : "assistant", content = message.Text });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (_options.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (_options.MaxTokens is { } maxTokens)
        {
            body[_options.MaxTokensParameter] = maxTokens;
        }

        // The extra fields of the settings, next to the main ones. They cannot replace a main field (checked at startup);
        // TryAdd keeps that true even if a check were ever loosened.
        foreach (var (name, value) in _extra)
        {
            body.TryAdd(name, value);
        }

        return JsonSerializer.Serialize(body);
    }

    // ---------------------------------------------------------------- retry (before the first word only)

    private async Task<ReplyStream> StartAsync(string body, long started, ReplyStats stats, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                stats.Reset(); // the numbers of a failed attempt do not count
                return await TryStartAsync(body, started, attempt, stats, ct);
            }
            catch (ModelUnavailableException ex) when (IsRetryable(ex.Failure) && attempt <= _options.MaxRetries)
            {
                _logger.LogWarning(
                    "Model request attempt {Attempt} failed ({Reason}, status {Status}); trying again.", attempt, ex.Failure, ex.StatusCode);
                await Task.Delay(Pause(attempt, ex), ct);
            }
            catch (ModelUnavailableException ex)
            {
                if (ex.Failure == ModelFailure.Rejected)
                {
                    _logger.LogError(
                        "The model provider refused the request (status {Status}). Check the Llm settings; the request is not repeated.", ex.StatusCode);
                }
                else
                {
                    _logger.LogWarning(
                        "Model request failed after {Attempt} attempt(s) ({Reason}, status {Status}); the fallback reply is used.", attempt, ex.Failure, ex.StatusCode);
                }

                throw;
            }
        }
    }

    private static bool IsRetryable(ModelFailure failure) => failure is not (ModelFailure.Rejected or ModelFailure.Busy);

    private TimeSpan Pause(int attempt, ModelUnavailableException failure)
    {
        if (failure.RetryAfter is { } hint)
        {
            return hint > MaxRetryAfter ? MaxRetryAfter : hint;
        }

        var factor = 0.75 + (Random.Shared.NextDouble() * 0.5); // jitter
        return TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs * Math.Pow(2, attempt - 1) * factor);
    }

    /// <summary>One attempt: connect, send, and wait for the FIRST word. The caller sees a started reply or a categorised failure.</summary>
    private async Task<ReplyStream> TryStartAsync(string body, long started, int attempt, ReplyStats stats, CancellationToken ct)
    {
        var attemptStarted = Stopwatch.GetTimestamp();
        var firstTimeout = Seconds(_options.FirstTokenTimeoutSeconds);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (_options.UsesApiKeyHeader)
            {
                // The key travels in the configured header ONLY: no Authorization header is sent in this mode.
                request.Headers.TryAddWithoutValidation(_options.AuthHeaderName, _options.ApiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(firstTimeout);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Classify(response); // the body is NOT read: it may carry anything
            }

            var remaining = firstTimeout - Stopwatch.GetElapsedTime(attemptStarted);
            Stream stream;
            using (var open = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                open.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                stream = await response.Content.ReadAsStreamAsync(open.Token);
            }

            var reply = new ReplyStream(
                response,
                new StreamReader(stream, Encoding.UTF8),
                Seconds(_options.IdleTimeoutSeconds),
                started + (long)(Seconds(_options.TotalTimeoutSeconds).TotalSeconds * Stopwatch.Frequency),
                attempt,
                stats);
            response = null; // owned by the reply stream from here on

            try
            {
                var left = firstTimeout - Stopwatch.GetElapsedTime(attemptStarted);
                var first = await reply.NextAsync(ct, left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1));
                if (first is null)
                {
                    throw new ModelUnavailableException(ModelFailure.BadAnswer); // ended without a single word
                }

                reply.SetFirst(first, Stopwatch.GetElapsedTime(started));
                return reply;
            }
            catch
            {
                await reply.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException(ModelFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new ModelUnavailableException(ModelFailure.Network); // the exception text is dropped on purpose
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static ModelUnavailableException Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status switch
        {
            408 => new ModelUnavailableException(ModelFailure.Timeout, status),
            429 => new ModelUnavailableException(ModelFailure.RateLimited, status) { RetryAfter = RetryAfterOf(response) },
            >= 500 => new ModelUnavailableException(ModelFailure.ServerError, status),
            _ => new ModelUnavailableException(ModelFailure.Rejected, status),
        };
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return header?.Date is { } date && date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : null;
    }

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(Math.Max(0.001, seconds));

    // ---------------------------------------------------------------- the stream

    internal enum EventKind
    {
        Skip,
        Content,
        Done,
        Bad,
    }

    /// <summary>What one line of the stream says. <c>ReasoningChars</c> is a LENGTH: the reasoning text itself is never returned.</summary>
    internal readonly record struct SseEvent(EventKind Kind, string? Text = null, int ReasoningChars = 0, int? CompletionTokens = null);

    /// <summary>
    /// One line of a server sent events stream. Only <c>delta.content</c> is ever returned as text. The reasoning fields
    /// (<c>reasoning</c>, <c>reasoning_content</c>) are only counted (characters) and dropped: they never reach a participant, a log or a file.
    /// Anything that is not a <c>data:</c> line, and any event without a word, is skipped.
    /// </summary>
    internal static SseEvent ParseLine(string line)
    {
        if (line.Length == 0 || line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
        {
            return new SseEvent(EventKind.Skip);
        }

        var payload = line[5..].Trim();
        if (payload == "[DONE]")
        {
            return new SseEvent(EventKind.Done);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            {
                return new SseEvent(EventKind.Bad);
            }

            int? tokens = root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("completion_tokens", out var count)
                && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var n)
                ? n
                : null;

            var reasoning = 0;
            string? text = null;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in ReasoningFields)
                {
                    if (delta.TryGetProperty(field, out var thought) && thought.ValueKind == JsonValueKind.String)
                    {
                        reasoning += thought.GetString()!.Length; // the length only
                    }
                }

                if (delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } word)
                {
                    text = word;
                }
            }

            return text is null
                ? new SseEvent(EventKind.Skip, null, reasoning, tokens) // role only, finish reason, usage, reasoning, an empty word
                : new SseEvent(EventKind.Content, text, reasoning, tokens);
        }
        catch (JsonException)
        {
            return new SseEvent(EventKind.Bad);
        }
    }

    private static readonly string[] ReasoningFields = ["reasoning", "reasoning_content"];

    private sealed class ReplyStream(HttpResponseMessage response, StreamReader reader, TimeSpan idle, long deadline, int attempts, ReplyStats stats) : IAsyncDisposable
    {
        private bool _done;

        public string First { get; private set; } = string.Empty;

        public TimeSpan FirstAfter { get; private set; }

        public int Attempts { get; } = attempts;

        public void SetFirst(string first, TimeSpan after)
        {
            First = first;
            FirstAfter = after;
        }

        /// <summary>The next word, or null when the reply is over. Waits at most <paramref name="budget"/> (default: the idle time) and never past the total deadline.</summary>
        public async Task<string?> NextAsync(CancellationToken ct, TimeSpan? budget = null)
        {
            if (_done)
            {
                return null;
            }

            var untilDeadline = TimeSpan.FromSeconds((deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            var wait = budget ?? idle;
            if (untilDeadline < wait)
            {
                wait = untilDeadline;
            }

            if (wait <= TimeSpan.Zero)
            {
                throw new ModelUnavailableException(ModelFailure.Timeout);
            }

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(wait);
                while (await reader.ReadLineAsync(limit.Token) is { } line)
                {
                    var parsed = ParseLine(line);
                    stats.ReasoningChars += parsed.ReasoningChars;
                    if (parsed.CompletionTokens is { } tokens)
                    {
                        stats.CompletionTokens = tokens;
                    }

                    switch (parsed.Kind)
                    {
                        case EventKind.Content:
                            return parsed.Text;
                        case EventKind.Done:
                            _done = true;
                            return null;
                        case EventKind.Bad:
                            throw new ModelUnavailableException(ModelFailure.BadAnswer);
                    }
                }

                _done = true; // closed without [DONE]: some servers do that
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ModelUnavailableException(ModelFailure.Timeout);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
            {
                throw new ModelUnavailableException(ModelFailure.Network);
            }
        }

        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The one place an <see cref="HttpClient"/> for the model is made: no redirects (a redirect could leave the allow list), no cookies.</summary>
public static class LlmHttpClient
{
    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public static HttpClient Create(HttpMessageHandler? handler = null) =>
        new(handler ?? CreateDefaultHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan, // the responder sets every deadline itself
        };
}
