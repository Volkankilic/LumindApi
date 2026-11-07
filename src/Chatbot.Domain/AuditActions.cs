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

    // ---- Admin panel (phase 7). Actor: Researcher (a failed sign-in of an unknown name: Anonymous). Never content.
    public const string AdminLoginSucceeded = "admin.login.succeeded";
    public const string AdminLoginFailed = "admin.login.failed";
    public const string AdminTotpFailed = "admin.totp.failed";
    public const string AdminRecoveryCodeUsed = "admin.recovery-code.used";
    public const string AdminAccountLocked = "admin.account.locked";
    public const string AdminLogout = "admin.logout";
    public const string AdminRefreshReuseDetected = "admin.refresh.reuse-detected";
    public const string AdminAccountCreated = "admin.account.created";
    public const string AdminAccountDisabled = "admin.account.disabled";
    public const string AdminAccountEnabled = "admin.account.enabled";
    public const string AdminPasswordChanged = "admin.password.changed";
    public const string AdminParticipantCreated = "admin.participant.created";
    public const string AdminParticipantCodeReset = "admin.participant.code-reset";
    public const string AdminParticipantRevoked = "admin.participant.revoked";
    public const string AdminParticipantReactivated = "admin.participant.reactivated";
    public const string AdminParticipantViewed = "admin.participant.viewed";
    public const string AdminStatsExported = "admin.stats.exported";
    public const string AdminCrisisViewed = "admin.crisis.viewed";
    public const string AdminCrisisAcknowledged = "admin.crisis.acknowledged";
    public const string AdminCrisisReopened = "admin.crisis.reopened";

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
        AdminLoginSucceeded, AdminLoginFailed, AdminTotpFailed, AdminRecoveryCodeUsed, AdminAccountLocked, AdminLogout,
        AdminRefreshReuseDetected, AdminAccountCreated, AdminAccountDisabled, AdminAccountEnabled, AdminPasswordChanged,
        AdminParticipantCreated, AdminParticipantCodeReset, AdminParticipantRevoked, AdminParticipantReactivated,
        AdminParticipantViewed, AdminStatsExported, AdminCrisisViewed, AdminCrisisAcknowledged, AdminCrisisReopened,
    ];
}
