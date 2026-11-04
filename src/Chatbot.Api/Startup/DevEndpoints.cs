using Chatbot.Application;
using Chatbot.Infrastructure.Dev;

namespace Chatbot.Api.Startup;

/// <summary>
/// Development ONLY. Mapped (and the service registered) only when the environment is Development, so in any other
/// environment the routes do not exist at all (404) and nothing here is in the container.
/// </summary>
public static class DevEndpoints
{
    public sealed record CreateTestParticipantRequest(string? State);

    public sealed record TestParticipantResponse(Guid ParticipantId, string Code);

    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dev/test-participants").AllowAnonymous().ExcludeFromDescription();

        group.MapPost(string.Empty, async (CreateTestParticipantRequest request, DevTestParticipantService service, CancellationToken ct) =>
        {
            if (!Enum.TryParse<TestParticipantState>(request.State, ignoreCase: true, out var state)
                || !Enum.IsDefined(state))
            {
                throw AppException.ValidationFailed();
            }

            var created = await service.CreateAsync(state, ct);
            return Results.Created($"/api/dev/test-participants/{created.ParticipantId}", new TestParticipantResponse(created.ParticipantId, created.Code));
        });

        group.MapDelete("{id:guid}", async (Guid id, DevTestParticipantService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        return app;
    }
}
