using Chatbot.Application.Abstractions;
using Chatbot.Domain;

namespace Chatbot.Application.Admin;

// ------------------------------------------------------------------ authentication

/// <param name="MfaToken">Short lived, single purpose: it only lets the second step (TOTP / recovery code) proceed.</param>
public sealed record AdminLoginResult(string MfaToken, int ExpiresInSeconds);

public sealed record AdminAuthResult(string AccessToken, int ExpiresInSeconds, string Username, IssuedRefreshToken Refresh);

/// <summary>Issues and reads the admin tokens (own signing key and audiences: a participant token never works here).</summary>
public interface IAdminTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) IssueAccess(Guid adminId, Guid familyId);

    (string Token, DateTimeOffset ExpiresAt) IssueMfa(Guid adminId);

    /// <summary>The admin id of a valid, unexpired "TOTP pending" token, otherwise null.</summary>
    Task<Guid?> ValidateMfaAsync(string? token);
}

public interface IAdminAuthService
{
    /// <summary>Step one. Every failure (unknown name, wrong password, disabled, no TOTP) is the same 401 `invalid-credentials`; a lock is a 429.</summary>
    Task<AdminLoginResult> LoginAsync(string? username, string? password, ClientInfo client, CancellationToken ct);

    /// <summary>Step two: a TOTP code OR a recovery code. Only here the access token and the refresh cookie are issued.</summary>
    Task<AdminAuthResult> CompleteAsync(string? mfaToken, string? code, string? recoveryCode, ClientInfo client, CancellationToken ct);

    Task<AdminAuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken ct);

    Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct);
}

/// <summary>Checked against the database on every admin request: an active account with TOTP and a session that was not revoked.</summary>
public interface IAdminAccess
{
    Task<bool> CheckAsync(Guid adminId, Guid familyId, CancellationToken ct);

    Task<string?> UsernameAsync(Guid adminId, CancellationToken ct);
}

// ------------------------------------------------------------------ participant management (no content, no nickname)

public sealed record CreateParticipantsInput(string? Label, string? LabelPrefix, int? Count);

/// <summary>The only place a plain code ever appears: the answer that created or reset it.</summary>
public sealed record IssuedParticipantCode(Guid Id, string Label, string Code);

public enum ParticipantSort { Label, Created, LastSeen, Messages }

public sealed record ParticipantListQuery(string? Search, ParticipantStatus? Status, int Page, int PageSize, ParticipantSort Sort, bool Descending);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public interface IAdminParticipantService
{
    Task<IReadOnlyList<IssuedParticipantCode>> CreateAsync(Guid adminId, CreateParticipantsInput input, ClientInfo client, CancellationToken ct);

    Task<IssuedParticipantCode> ResetCodeAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct);

    Task RevokeAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct);

    Task ReactivateAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct);

    Task<PagedResult<ParticipantStats>> ListAsync(ParticipantListQuery query, CancellationToken ct);

    /// <summary>One participant with the conversation list. Written to the audit log (a participant's usage was looked at).</summary>
    Task<ParticipantDetail> GetAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct);
}

// ------------------------------------------------------------------ statistics

public enum ConversationState { Ongoing, Completed, Abandoned }

/// <summary>Counts and times only. There is no text field in any of these records, on purpose.</summary>
public sealed record ConversationStats(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double? DurationMinutes,
    ConversationState State,
    int UserMessages,
    int AssistantMessages,
    double EstimatedActiveMinutes,
    DateTimeOffset? LastActivityAt);

public sealed record ParticipantStats(
    Guid Id,
    string? Label,
    ParticipantStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FirstLoginAt,
    DateTimeOffset? LastLoginAt,
    int LoginCount,
    int ConversationCount,
    int OngoingConversations,
    int CompletedConversations,
    int AbandonedConversations,
    int UserMessages,
    int AssistantMessages,
    DateTimeOffset? LastActivityAt,
    double EstimatedActiveMinutes);

public sealed record ParticipantDetail(ParticipantStats Stats, IReadOnlyList<ConversationStats> Conversations);

public sealed record ActivityPoint(DateTimeOffset PeriodStart, int ConversationsStarted, int UserMessages, int ActiveParticipants);

public sealed record StatsSummary(
    int Invited,
    int Active,
    int Revoked,
    int NeverSignedIn,
    int Conversations,
    int CompletedConversations,
    double? AverageCompletedMinutes,
    IReadOnlyList<ActivityPoint> Daily,
    IReadOnlyList<ActivityPoint> Weekly);

public interface IAdminStatsService
{
    Task<StatsSummary> SummaryAsync(CancellationToken ct);

    /// <summary>The per-participant statistics as CSV text (label, counts, times, estimated active time). Written to the audit log.</summary>
    Task<string> ExportCsvAsync(Guid adminId, ClientInfo client, CancellationToken ct);
}

// ------------------------------------------------------------------ crisis flags

/// <summary>A conversation with crisis flags. Time and counts only: never the text, a phrase or a category.</summary>
public sealed record CrisisGroup(
    Guid ConversationId,
    string? ParticipantLabel,
    DateTimeOffset FirstFlaggedAt,
    DateTimeOffset LastFlaggedAt,
    int FlaggedCount,
    int OpenCount,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy);

public sealed record CrisisOverview(int OpenConversations, int OpenMessages, IReadOnlyList<CrisisGroup> Items);

public interface IAdminCrisisService
{
    Task<CrisisOverview> ListAsync(Guid adminId, bool openOnly, ClientInfo client, CancellationToken ct);

    /// <summary>Closes every open crisis message of the conversation (one audit entry per message). Returns how many were closed.</summary>
    Task<int> AcknowledgeAsync(Guid adminId, Guid conversationId, ClientInfo client, CancellationToken ct);

    /// <summary>Takes the acknowledgement back (one audit entry per message). Returns how many were reopened.</summary>
    Task<int> ReopenAsync(Guid adminId, Guid conversationId, ClientInfo client, CancellationToken ct);
}
