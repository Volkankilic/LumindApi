using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.TestSupport;

/// <summary>
/// Integration tests share ONE database with the demo and the seed data. They therefore create participants labelled
/// `itest-...` and, when they finish, delete only those participants and the rows that belong to them. This is the only
/// place that deletes consent and audit rows (production code never does), and it refuses to touch anything whose label
/// does not start with `itest-`.
/// </summary>
public static class TestDataCleanup
{
    public const string LabelPrefix = "itest-";

    public static string NewLabel() => LabelPrefix + Guid.NewGuid().ToString("N");

    public static bool IsTestLabel(string? label) =>
        label is not null && label.StartsWith(LabelPrefix, StringComparison.Ordinal) && label.Length > LabelPrefix.Length;

    /// <summary>Throws when any participant is not a test participant. Nothing is deleted before this check passes.</summary>
    public static void EnsureAllAreTestParticipants(IEnumerable<(Guid Id, string? Label)> participants)
    {
        var foreign = participants.Where(p => !IsTestLabel(p.Label)).Select(p => p.Id).ToList();
        if (foreign.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refusing to delete {foreign.Count} participant(s) whose label does not start with '{LabelPrefix}'.");
        }
    }

    /// <summary>
    /// Deletes `itest-` admins: their acknowledgements, audit rows (as actor or target), then the admin (sessions and recovery codes
    /// cascade). Refuses a user name that does not start with `itest-`.
    /// </summary>
    public static async Task DeleteAdminsAsync(AppDbContext db, IReadOnlyCollection<Guid> adminIds)
    {
        if (adminIds.Count == 0)
        {
            return;
        }

        var found = await db.AdminUsers.AsNoTracking().Where(a => adminIds.Contains(a.Id)).Select(a => new { a.Id, a.Username }).ToListAsync();
        var foreign = found.Where(a => !a.Username.StartsWith(LabelPrefix, StringComparison.Ordinal)).ToList();
        if (foreign.Count > 0)
        {
            throw new InvalidOperationException($"Refusing to delete {foreign.Count} admin(s) whose user name does not start with {LabelPrefix}.");
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.CrisisAcknowledgements.Where(a => adminIds.Contains(a.AdminId)).ExecuteDeleteAsync();
        await db.AuditLogs.Where(a => (a.ActorId != null && adminIds.Contains(a.ActorId.Value))
            || (a.TargetId != null && adminIds.Contains(a.TargetId.Value))).ExecuteDeleteAsync();
        await db.AdminUsers.Where(a => adminIds.Contains(a.Id) && a.Username.StartsWith(LabelPrefix)).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    /// <summary>Deletes the given test participants, children first (foreign key order): consent, audit, tokens, conversations (messages cascade), then the participants.</summary>
    public static async Task DeleteParticipantsAsync(AppDbContext db, IReadOnlyCollection<Guid> participantIds)
    {
        if (participantIds.Count == 0)
        {
            return;
        }

        var found = await db.Participants.AsNoTracking()
            .Where(p => participantIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Label })
            .ToListAsync();
        EnsureAllAreTestParticipants(found.Select(p => (p.Id, p.Label)));

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ConsentRecords.Where(r => participantIds.Contains(r.ParticipantId)).ExecuteDeleteAsync();
        await db.AuditLogs.Where(a => (a.ActorId != null && participantIds.Contains(a.ActorId.Value))
            || (a.TargetId != null && participantIds.Contains(a.TargetId.Value))).ExecuteDeleteAsync();
        await db.RefreshTokens.Where(t => participantIds.Contains(t.ParticipantId)).ExecuteDeleteAsync();
        await db.Conversations.Where(c => participantIds.Contains(c.ParticipantId)).ExecuteDeleteAsync();
        await db.Participants.Where(p => participantIds.Contains(p.Id) && p.Label != null && p.Label.StartsWith(LabelPrefix)).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }
}
