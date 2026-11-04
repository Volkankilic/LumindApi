using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Participants;

public sealed class ParticipantAccess(AppDbContext db, DocumentsOptions documents) : IParticipantAccess
{
    public async Task<AccessStatus> CheckAsync(Guid participantId, CancellationToken ct)
    {
        var status = await db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId)
            .Select(p => (ParticipantStatus?)p.Status)
            .FirstOrDefaultAsync(ct);

        // Unknown, revoked and deleted participants lose access at once, whatever their token still says.
        if (status is null or ParticipantStatus.Revoked or ParticipantStatus.Deleted)
        {
            return AccessStatus.Denied;
        }

        var snapshot = await ParticipantStates.LoadSnapshotAsync(db, participantId, ct);
        return ConsentRules.IsOnboarded(snapshot, documents.ToVersions())
            ? AccessStatus.Ok
            : AccessStatus.NotOnboarded;
    }
}
