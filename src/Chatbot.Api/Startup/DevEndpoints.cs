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

    public sealed record TestAdminResponse(Guid AdminId, string Username, string Password, string TotpSecret, string RecoveryCode);

    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        // Synthetic admins for the end-to-end tests (user name starts with itest-). The TOTP code is computed by the test itself.
        var admins = app.MapGroup("/api/dev/test-admins").AllowAnonymous().ExcludeFromDescription();
        admins.MapPost(string.Empty, async (DevTestAdminService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(ct);
            return Results.Created($"/api/dev/test-admins/{created.AdminId}", new TestAdminResponse(created.AdminId, created.Username, created.Password, created.TotpSecretBase32, created.RecoveryCode));
        });
        admins.MapDelete("{id:guid}", async (Guid id, DevTestAdminService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

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

        group.MapPost("{id:guid}/adopt", async (Guid id, DevTestParticipantService service, CancellationToken ct) =>
        {
            await service.AdoptAsync(id, ct);
            return Results.NoContent();
        });

        group.MapDelete("{id:guid}", async (Guid id, DevTestParticipantService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        return app;
    }
}
