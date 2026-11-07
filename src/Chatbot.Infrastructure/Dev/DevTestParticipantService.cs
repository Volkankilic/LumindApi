using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Dev;

public enum TestParticipantState { New, Ready, Withdrawn, Revoked, Outdated }

public sealed record CreatedTestParticipant(Guid ParticipantId, string Code);

/// <summary>
/// Development ONLY (registered in Development only, see AddInfrastructure). Creates and removes synthetic participants
/// for the end-to-end tests. Production code never deletes consent or audit rows; this is the one place that does, and
/// it refuses everything whose label does not start with `itest-`.
/// </summary>
public sealed class DevTestParticipantService(
    AppDbContext db,
    ISecretHasher hasher,
    DocumentsOptions documents,
    TimeProvider clock)
{
    public const string LabelPrefix = "itest-";

    private const string OldVersion = "0.0-eski";

    public static string NewLabel() => $"{LabelPrefix}e2e-{Guid.NewGuid():N}";

    public static bool IsTestLabel(string? label) =>
        label is not null && label.StartsWith(LabelPrefix, StringComparison.Ordinal) && label.Length > LabelPrefix.Length;

    public async Task<CreatedTestParticipant> CreateAsync(TestParticipantState state, CancellationToken ct)
    {
        var code = NewCode();
        var now = clock.GetUtcNow();
        var versions = documents.ToVersions();
        var participant = new Participant
        {
            Id = Guid.CreateVersion7(),
            CodeHash = hasher.HashCode(code),
            Label = NewLabel(),
            Status = state switch
            {
                TestParticipantState.New => ParticipantStatus.Invited,
                TestParticipantState.Revoked => ParticipantStatus.Revoked,
                _ => ParticipantStatus.Active,
            },
            Nickname = state is TestParticipantState.Ready or TestParticipantState.Outdated ? "Deneme" : null,
            CreatedAt = now,
            LastSeenAt = state is TestParticipantState.New ? null : now,
        };
        db.Participants.Add(participant);

        // Records are written a second apart: the latest record of a type is its current state.
        var step = 0;
        void Consent(ConsentType type, string version, bool granted) =>
            db.ConsentRecords.Add(new ConsentRecord
            {
                Id = Guid.CreateVersion7(),
                ParticipantId = participant.Id,
                Type = type,
                DocumentVersion = version,
                Granted = granted,
                Timestamp = now.AddSeconds(step++ - 60),
            });

        switch (state)
        {
            case TestParticipantState.Ready:
                Consent(ConsentType.PrivacyNotice, versions.PrivacyNotice, true);
                Consent(ConsentType.ExplicitConsent, versions.Consent, true);
                break;
            case TestParticipantState.Withdrawn:
                Consent(ConsentType.PrivacyNotice, versions.PrivacyNotice, true);
                Consent(ConsentType.ExplicitConsent, versions.Consent, true);
                Consent(ConsentType.ExplicitConsent, versions.Consent, false);
                break;
            case TestParticipantState.Outdated:
                Consent(ConsentType.PrivacyNotice, OldVersion, true);
                Consent(ConsentType.ExplicitConsent, OldVersion, true);
                break;
        }

        await db.SaveChangesAsync(ct);
        return new CreatedTestParticipant(participant.Id, code);
    }

    /// <summary>
    /// A participant created through the ADMIN panel by an end-to-end test (its label starts with "e2e-"; the panel refuses the
    /// "itest-" prefix) is given an `itest-` label, so the normal cleanup can delete it. Anything else is refused: this can never
    /// relabel (and so never delete) a real participant.
    /// </summary>
    public async Task AdoptAsync(Guid participantId, CancellationToken ct)
    {
        var participant = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct) ?? throw AppException.NotFound();
        if (participant.Label is null || !participant.Label.StartsWith("e2e-", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("forbidden", 403);
        }

        participant.Label = NewLabel();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a test participant and everything that belongs to it, children first (foreign key order).</summary>
    public async Task DeleteAsync(Guid participantId, CancellationToken ct)
    {
        var label = await db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId)
            .Select(p => p.Label)
            .SingleOrDefaultAsync(ct);
        var exists = await db.Participants.AsNoTracking().AnyAsync(p => p.Id == participantId, ct);
        if (!exists)
        {
            throw AppException.NotFound();
        }

        if (!IsTestLabel(label))
        {
            throw new AppException("forbidden", 403);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ConsentRecords.Where(r => r.ParticipantId == participantId).ExecuteDeleteAsync(ct);
        await db.AuditLogs.Where(a => a.ActorId == participantId || a.TargetId == participantId).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(t => t.ParticipantId == participantId).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.ParticipantId == participantId).ExecuteDeleteAsync(ct); // messages cascade
        await db.Participants
            .Where(p => p.Id == participantId && p.Label != null && p.Label.StartsWith(LabelPrefix))
            .ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string NewCode()
    {
        var chars = new char[ParticipantCode.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = ParticipantCode.Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(ParticipantCode.Alphabet.Length)];
        }

        return new string(chars);
    }
}
