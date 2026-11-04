using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Participants;

internal static class ParticipantStates
{
    public static async Task<ConsentSnapshot> LoadSnapshotAsync(AppDbContext db, Guid participantId, CancellationToken ct)
    {
        var records = await db.ConsentRecords.AsNoTracking()
            .Where(r => r.ParticipantId == participantId)
            .ToListAsync(ct);
        return ConsentRules.Snapshot(records);
    }

    public static async Task<ParticipantState> LoadStateAsync(AppDbContext db, Participant participant, CancellationToken ct) =>
        ConsentRules.ComputeState(participant, await LoadSnapshotAsync(db, participant.Id, ct));
}
