using Chatbot.Domain.Entities;

namespace Chatbot.Domain.Rules;

/// <summary>Document versions the server currently requires (from configuration).</summary>
public sealed record DocumentVersions(string PrivacyNotice, string Consent);

public static class ConsentRules
{
    /// <summary>The latest record of each type is the current state of that type (append-only log).</summary>
    public static ConsentSnapshot Snapshot(IEnumerable<ConsentRecord> records)
    {
        // Order by time, then by id (UUIDv7 is time ordered) so records written in the same tick stay deterministic.
        var latest = records
            .GroupBy(r => r.Type)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id).First());

        latest.TryGetValue(ConsentType.PrivacyNotice, out var notice);
        latest.TryGetValue(ConsentType.ExplicitConsent, out var explicitConsent);
        latest.TryGetValue(ConsentType.ResearchUse, out var research);

        return new ConsentSnapshot(
            notice?.DocumentVersion,
            explicitConsent is null ? null : (explicitConsent.DocumentVersion, explicitConsent.Granted),
            research?.Granted);
    }

    public static ParticipantState ComputeState(Participant participant, ConsentSnapshot snapshot)
    {
        // A withdrawn (or never given) consent is null. A consent given for an OLD version is still
        // reported with that version, so the client can tell it has to ask again (see HasValidConsent).
        var consent = snapshot.LatestExplicitConsent is { Granted: true } given
            ? new ConsentState(given.Version, Processing: true, ResearchUse: snapshot.LatestResearchUseGranted ?? false)
            : null;

        return new ParticipantState(participant.Id, participant.Nickname, snapshot.LatestNoticeVersion, consent);
    }

    /// <summary>Valid consent: the latest explicit consent is granted AND for the server's current version.</summary>
    public static bool HasValidConsent(ConsentSnapshot snapshot, DocumentVersions current) =>
        snapshot.LatestExplicitConsent is { Granted: true } given
        && string.Equals(given.Version, current.Consent, StringComparison.Ordinal);

    public static bool IsNoticeCurrent(ConsentSnapshot snapshot, DocumentVersions current) =>
        string.Equals(snapshot.LatestNoticeVersion, current.PrivacyNotice, StringComparison.Ordinal);

    /// <summary>What the OnboardedParticipant policy requires: current notice AND valid consent.</summary>
    public static bool IsOnboarded(ConsentSnapshot snapshot, DocumentVersions current) =>
        IsNoticeCurrent(snapshot, current) && HasValidConsent(snapshot, current);
}
