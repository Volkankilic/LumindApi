namespace Chatbot.Domain.Rules;

/// <summary>Current consent as the client sees it. Null when never given or withdrawn.</summary>
public sealed record ConsentState(string Version, bool Processing, bool ResearchUse);

/// <summary>Exact counterpart of the client's ParticipantState type.</summary>
public sealed record ParticipantState(
    Guid ParticipantId,
    string? Nickname,
    string? NoticeAcknowledgedVersion,
    ConsentState? Consent);

/// <summary>The few facts the state is computed from (one row per consent type is enough: the latest one).</summary>
public sealed record ConsentSnapshot(
    string? LatestNoticeVersion,
    (string Version, bool Granted)? LatestExplicitConsent,
    bool? LatestResearchUseGranted);
