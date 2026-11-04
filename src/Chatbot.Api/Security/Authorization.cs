using Chatbot.Api.Http;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Chatbot.Api.Security;

public static class Policies
{
    /// <summary>A valid token of a participant who still exists and is not revoked or deleted.</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Authenticated, and the CURRENT privacy notice was acknowledged and a valid consent exists.</summary>
    public const string OnboardedParticipant = "OnboardedParticipant";
}

public sealed record ParticipantAccessRequirement(bool RequireOnboarded) : IAuthorizationRequirement;

/// <summary>
/// Checked against the database on every request (not against a token claim), so a revoked participant or a
/// withdrawn consent takes effect immediately.
/// </summary>
public sealed class ParticipantAccessHandler(IParticipantAccess access) : AuthorizationHandler<ParticipantAccessRequirement>
{
    public const string DeniedReason = "denied";
    public const string ConsentRequiredReason = "consent-required";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ParticipantAccessRequirement requirement)
    {
        var id = context.User.ParticipantId();
        if (id == Guid.Empty)
        {
            context.Fail(new AuthorizationFailureReason(this, DeniedReason));
            return;
        }

        var ct = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        switch (await access.CheckAsync(id, ct))
        {
            case AccessStatus.Ok:
                context.Succeed(requirement);
                break;
            case AccessStatus.NotOnboarded when !requirement.RequireOnboarded:
                context.Succeed(requirement);
                break;
            case AccessStatus.NotOnboarded:
                context.Fail(new AuthorizationFailureReason(this, ConsentRequiredReason));
                break;
            default:
                context.Fail(new AuthorizationFailureReason(this, DeniedReason));
                break;
        }
    }
}

/// <summary>Turns authorization outcomes into the documented ProblemDetails (401 unauthorized, 403 consent-required).</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var reasons = authorizeResult.AuthorizationFailure?.FailureReasons.Select(r => r.Message) ?? [];
        if (reasons.Contains(ParticipantAccessHandler.ConsentRequiredReason))
        {
            await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, ProblemCodes.ConsentRequired);
            return;
        }

        // No token, a bad token, or a participant who is gone / revoked / deleted: all the same 401.
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized);
    }
}
