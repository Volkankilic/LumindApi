using Chatbot.Api.Security;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chatbot.Api.Controllers;

/// <summary>The signed-in participant: state, consent, nickname, erasure. A participant only ever sees their own data.</summary>
[ApiController]
[Authorize(Policy = Policies.Authenticated)]
[Route("api")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class MeController(IParticipantService participants, AuthOptions auth) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType<ParticipantState>(StatusCodes.Status200OK)]
    public Task<ParticipantState> Get(CancellationToken ct) =>
        participants.GetStateAsync(HttpContext.ParticipantId(), HttpContext.Client(), ct);

    /// <summary>
    /// Records the privacy notice and/or consents (1-3 records, atomic). A document version that is not the server's
    /// current one is a 409 `stale-document-version`. Withdrawing the explicit consent can delete content (see ADR 004).
    /// </summary>
    [HttpPost("consents")]
    [ProducesResponseType<ParticipantState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ParticipantState> RecordConsents(ConsentRequest request, CancellationToken ct)
    {
        var records = request.Records ?? throw AppException.ValidationFailed();
        var inputs = records
            .Select(r => new ConsentInput(r.Type, r.Version ?? string.Empty, r.Granted))
            .ToList();
        return participants.RecordConsentsAsync(HttpContext.ParticipantId(), inputs, HttpContext.Client(), ct);
    }

    [HttpPut("me/nickname")]
    [ProducesResponseType<ParticipantState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ParticipantState> SetNickname(NicknameRequest request, CancellationToken ct) =>
        participants.SetNicknameAsync(HttpContext.ParticipantId(), request.Nickname, HttpContext.Client(), ct);

    /// <summary>Deletes the participant's content for good and keeps an anonymous trace (code hash, consent log, audit log).</summary>
    [HttpDelete("me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<NoContentResult> Delete(CancellationToken ct)
    {
        await participants.DeleteAsync(HttpContext.ParticipantId(), HttpContext.Client(), ct);
        Response.Cookies.Delete(AuthController.RefreshCookie, AuthController.RefreshCookieDeleteOptions(auth));
        return NoContent();
    }
}
