using Chatbot.Api.Http;
using Chatbot.Api.Security;
using Chatbot.Api.Startup;
using Chatbot.Application;
using Chatbot.Application.Chat;
using Chatbot.Application.Conversations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Chatbot.Api.Controllers;

/// <summary>
/// Everything here needs a signed-in participant with a current notice and a valid consent. Someone else's conversation
/// and one that does not exist are the same 404.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.OnboardedParticipant)]
[Route("api/conversations")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public class ConversationsController(IConversationService conversations, IChatService chat) : ControllerBase
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    /// <summary>Starts a conversation. The participant's previous active conversation ends in the same step.</summary>
    [HttpPost]
    [ProducesResponseType<ConversationDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ConversationDto>> Start(CancellationToken ct)
    {
        var created = await conversations.StartAsync(HttpContext.ParticipantId(), ct);
        return Created($"/api/conversations/{created.Id}", created);
    }

    /// <summary>Newest first. No message content.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ConversationSummaryDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<ConversationSummaryDto>> List(CancellationToken ct) =>
        conversations.ListAsync(HttpContext.ParticipantId(), ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ConversationDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ConversationDetailDto> Get(Guid id, CancellationToken ct) =>
        conversations.GetAsync(HttpContext.ParticipantId(), id, ct);

    /// <summary>Idempotent: the end time is written only by the first call.</summary>
    [HttpPost("{id:guid}/end")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<NoContentResult> End(Guid id, CancellationToken ct)
    {
        await conversations.EndAsync(HttpContext.ParticipantId(), id, ct);
        return NoContent();
    }

    /// <summary>
    /// Sends a message and streams the reply as server-sent events: `accepted`, optionally `crisis`, `delta`*, `done`,
    /// or `error`. Failures BEFORE `accepted` (422, 404, 409, 429, 503) are plain HTTP ProblemDetails responses
    /// ("the message did not arrive"); after `accepted` they are `error` events ("the reply was cut off").
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    [EnableRateLimiting(ApiServices.SendMessageLimiter)]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task SendMessage(Guid id, SendMessageRequest request, CancellationToken ct)
    {
        if (request.ClientMessageId == Guid.Empty)
        {
            throw AppException.ValidationFailed();
        }

        await using var events = chat
            .SendAsync(HttpContext.ParticipantId(), id, request.Text, request.ClientMessageId, ct)
            .GetAsyncEnumerator(ct);

        // The first event is `accepted`. Asking for it runs the whole "before accepted" part: any failure is thrown here,
        // before a single response byte is written, and the exception middleware answers with a normal HTTP error.
        if (!await events.MoveNextAsync())
        {
            return;
        }

        var sse = new SseWriter(Response);
        sse.Start(HttpContext);

        using var pings = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAlive = KeepAliveAsync(sse, pings.Token);
        try
        {
            await sse.WriteAsync(events.Current, ct);
            while (await events.MoveNextAsync())
            {
                await sse.WriteAsync(events.Current, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The client closed the connection. Disposing the enumerator (below) lets the pipeline keep the partial reply as "stopped".
        }
        catch (IOException)
        {
            // Same: the connection broke while writing.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // After `accepted` a failure is an event, never a changed status code. No detail goes out.
            var code = ex is AppException known ? known.Code : "unknown";
            try
            {
                await sse.WriteAsync(new ChatEvent.Error(code, (ex as AppException)?.RetryAfterSeconds), ct);
            }
            catch (Exception)
            {
                // The connection is gone as well.
            }
        }
        finally
        {
            await pings.CancelAsync();
            await keepAlive;
        }
    }

    private static async Task KeepAliveAsync(SseWriter sse, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(PingInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await sse.WritePingAsync(ct);
            }
        }
        catch (Exception)
        {
            // Cancelled or the connection is gone: the keep-alive simply ends.
        }
    }
}
