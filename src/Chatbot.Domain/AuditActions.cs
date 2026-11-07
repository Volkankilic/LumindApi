namespace Chatbot.Domain;

/// <summary>
/// Every audit event name, in one place. The same list, with the reason each one is kept, is in
/// docs/kvkk/islem-kayitlari.md (a test keeps the two in step). Events never carry content: no message text, nickname,
/// code or raw IP.
/// </summary>
public static class AuditActions
{
    public const string AuthCodeFailed = "auth.code.failed";
    public const string AuthCodeSucceeded = "auth.code.succeeded";
    public const string AuthRefreshReuseDetected = "auth.refresh.reuse-detected";
    public const string AuthLogout = "auth.logout";
    public const string AuthFailureBudgetExceeded = "auth.failure-budget.exceeded";

    public const string ConsentNoticeGranted = "consent.privacynotice.granted";
    public const string ConsentExplicitGranted = "consent.explicitconsent.granted";
    public const string ConsentExplicitWithdrawn = "consent.explicitconsent.withdrawn";
    public const string ConsentResearchGranted = "consent.researchuse.granted";
    public const string ConsentResearchWithdrawn = "consent.researchuse.withdrawn";

    public const string ParticipantNicknameSet = "participant.nickname.set";
    public const string ParticipantDeleted = "participant.deleted";

    /// <summary>Reserved for the admin panel (phase 7): the code of a participant is revoked.</summary>
    public const string ParticipantRevoked = "participant.revoked";

    public const string ConversationStarted = "conversation.started";
    public const string ConversationEnded = "conversation.ended";
    public const string ConversationRead = "conversation.read";
    public const string HistoryListed = "conversation.listed";

    public const string RetentionRun = "retention.run";

    /// <summary>A user message was flagged by crisis detection. Carries the participant and the conversation only: no text, phrase or category.</summary>
    public const string CrisisDetected = "crisis.detected";

    /// <summary>The name written for a consent record, e.g. consent.explicitconsent.withdrawn.</summary>
    public static string Consent(ConsentType type, bool granted) =>
        $"consent.{type.ToString().ToLowerInvariant()}.{(granted ? "granted" : "withdrawn")}";

    public static IReadOnlyList<string> All { get; } =
    [
        AuthCodeFailed, AuthCodeSucceeded, AuthRefreshReuseDetected, AuthLogout, AuthFailureBudgetExceeded,
        ConsentNoticeGranted, ConsentExplicitGranted, ConsentExplicitWithdrawn, ConsentResearchGranted, ConsentResearchWithdrawn,
        ParticipantNicknameSet, ParticipantDeleted, ParticipantRevoked,
        ConversationStarted, ConversationEnded, ConversationRead, HistoryListed,
        RetentionRun, CrisisDetected,
    ];
}
