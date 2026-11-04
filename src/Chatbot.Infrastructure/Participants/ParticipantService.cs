using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Participants;

public sealed class ParticipantService(
    AppDbContext db,
    IAuditWriter audit,
    DocumentsOptions documents,
    PrivacyOptions privacy,
    TimeProvider clock) : IParticipantService
{
    public async Task<ParticipantState> GetStateAsync(Guid participantId, ClientInfo client, CancellationToken ct) =>
        await ParticipantStates.LoadStateAsync(db, await FindAsync(participantId, ct), ct);

    public async Task<ParticipantState> RecordConsentsAsync(
        Guid participantId,
        IReadOnlyList<ConsentInput> records,
        ClientInfo client,
        CancellationToken ct)
    {
        var versions = documents.ToVersions();
        if (records.Count is < 1 or > 3 || records.Select(r => r.Type).Distinct().Count() != records.Count)
        {
            throw AppException.ValidationFailed();
        }

        foreach (var record in records)
        {
            // The notice is an event ("was informed"), never a refusal.
            if (record.Type == ConsentType.PrivacyNotice && !record.Granted)
            {
                throw AppException.ValidationFailed();
            }

            var current = record.Type == ConsentType.PrivacyNotice ? versions.PrivacyNotice : versions.Consent;
            if (!string.Equals(record.Version, current, StringComparison.Ordinal))
            {
                throw AppException.StaleDocumentVersion();
            }
        }

        _ = await FindAsync(participantId, ct); // 401 when the participant is gone
        var before = await ParticipantStates.LoadSnapshotAsync(db, participantId, ct);

        // Research use is only possible with a valid explicit consent: the stored one, or one given in this very request.
        var explicitInRequest = records.FirstOrDefault(r => r.Type == ConsentType.ExplicitConsent);
        var consentValidAfter = explicitInRequest is not null
            ? explicitInRequest.Granted
            : ConsentRules.HasValidConsent(before, versions);
        if (records.Any(r => r.Type == ConsentType.ResearchUse) && !consentValidAfter)
        {
            throw AppException.ValidationFailed();
        }

        var now = clock.GetUtcNow();
        foreach (var record in records)
        {
            db.ConsentRecords.Add(new ConsentRecord
            {
                Id = Guid.CreateVersion7(),
                ParticipantId = participantId,
                Type = record.Type,
                DocumentVersion = record.Version,
                Granted = record.Granted,
                Timestamp = now,
            });
        }

        var withdrawn = explicitInRequest is { Granted: false };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (withdrawn && privacy.DeleteContentOnWithdrawal)
        {
            // TODO(volkan): withdrawal deletes nickname and conversations (safe default); the final rule needs legal advice.
            await DeleteContentAsync(participantId, ct);
        }

        await tx.CommitAsync(ct);

        foreach (var record in records)
        {
            var action = AuditActions.Consent(record.Type, record.Granted);
            await audit.WriteAsync(ActorType.Participant, participantId, action, nameof(ConsentRecord), null, client.Ip, ct);
        }

        db.ChangeTracker.Clear();
        return await ParticipantStates.LoadStateAsync(db, await FindAsync(participantId, ct), ct);
    }

    public async Task<ParticipantState> SetNicknameAsync(Guid participantId, string? nickname, ClientInfo client, CancellationToken ct)
    {
        var participant = await FindAsync(participantId, ct);
        var snapshot = await ParticipantStates.LoadSnapshotAsync(db, participantId, ct);
        if (!ConsentRules.HasValidConsent(snapshot, documents.ToVersions()))
        {
            throw AppException.ConsentRequired();
        }

        if (NicknameRules.Validate(nickname) is not null)
        {
            throw AppException.ValidationFailed();
        }

        participant.Nickname = NicknameRules.Clean(nickname!);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(ActorType.Participant, participantId, AuditActions.ParticipantNicknameSet, null, null, client.Ip, ct);
        return ConsentRules.ComputeState(participant, snapshot);
    }

    public async Task DeleteAsync(Guid participantId, ClientInfo client, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DeleteContentAsync(participantId, ct);
        await db.RefreshTokens
            .Where(t => t.ParticipantId == participantId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        // An anonymous trace stays: the code hash (so the code never works again), the consent log and the audit log.
        var updated = await db.Participants
            .Where(p => p.Id == participantId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, ParticipantStatus.Deleted)
                .SetProperty(p => p.Nickname, (string?)null), ct);
        if (updated != 1)
        {
            throw AppException.Unauthorized();
        }

        await tx.CommitAsync(ct);
        await audit.WriteAsync(ActorType.Participant, participantId, AuditActions.ParticipantDeleted, null, null, client.Ip, ct);
    }

    /// <summary>Deletes conversations (their messages go with them, by foreign key cascade) and the nickname.</summary>
    private async Task DeleteContentAsync(Guid participantId, CancellationToken ct)
    {
        await db.Conversations.Where(c => c.ParticipantId == participantId).ExecuteDeleteAsync(ct);
        await db.Participants
            .Where(p => p.Id == participantId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Nickname, (string?)null), ct);
    }

    private async Task<Participant> FindAsync(Guid participantId, CancellationToken ct) =>
        await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct)
        ?? throw AppException.Unauthorized();
}
