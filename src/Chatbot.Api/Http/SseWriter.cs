using System.Text;
using System.Text.Json;
using Chatbot.Application.Chat;

namespace Chatbot.Api.Http;

/// <summary>
/// Writes server-sent events: `event: name`, `data: one line of JSON`, blank line, flushed after every event so
/// nothing waits in a buffer. A lock keeps the keep-alive comment from interleaving with an event.
/// </summary>
public sealed class SseWriter(HttpResponse response)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Start(HttpContext context)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no"; // proxies (nginx style) must not buffer the stream
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    public Task WriteAsync(ChatEvent chatEvent, CancellationToken ct)
    {
        var (name, data) = chatEvent switch
        {
            ChatEvent.Accepted a => ("accepted", (object)new { userMessageId = a.UserMessageId }),
            ChatEvent.Crisis => ("crisis", new { }),
            ChatEvent.Delta d => ("delta", new { text = d.Text }),
            ChatEvent.Done d => ("done", new { assistantMessageId = d.AssistantMessageId }),
            ChatEvent.Error e => ("error", new { code = e.Code, retryAfterSeconds = e.RetryAfterSeconds }),
            _ => throw new ArgumentOutOfRangeException(nameof(chatEvent)),
        };
        return WriteRawAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
    }

    /// <summary>A comment line keeps the connection alive through idle proxies.</summary>
    public Task WritePingAsync(CancellationToken ct) => WriteRawAsync(": ping\n\n", ct);

    private async Task WriteRawAsync(string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await response.Body.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
