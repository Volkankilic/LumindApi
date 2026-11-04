using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;

namespace Chatbot.Application.Abstractions;

/// <summary>Keyed hashing. Plain codes, tokens and IPs are never stored.</summary>
public interface ISecretHasher
{
    /// <summary>HMAC-SHA256(normalized code, Auth:CodePepper), hex.</summary>
    string HashCode(string normalizedCode);

    /// <summary>SHA-256 of a (high entropy, random) refresh token, hex.</summary>
    string HashRefreshToken(string token);

    /// <summary>HMAC-SHA256(ip, Privacy:IpHashKey), hex. Null when the IP is unknown.</summary>
    string? HashIp(string? ip);
}

public interface IAuditWriter
{
    /// <summary>Writes one audit entry. Never pass content (message text, nickname, code, token).</summary>
    Task WriteAsync(
        ActorType actorType,
        Guid? actorId,
        string action,
        string? targetType,
        Guid? targetId,
        string? clientIp,
        CancellationToken ct);
}

public sealed record ClientInfo(string? Ip);

public sealed record IssuedRefreshToken(string Token, DateTimeOffset ExpiresAt);

public sealed record AuthResult(
    string AccessToken,
    int ExpiresInSeconds,
    ParticipantState State,
    IssuedRefreshToken Refresh);

public interface IAuthService
{
    /// <summary>Code sign-in. Every failure is the same <see cref="AppException.InvalidCode"/>.</summary>
    Task<AuthResult> SignInAsync(string? code, ClientInfo client, CancellationToken ct);

    /// <summary>Rotates the refresh token. A spent or revoked token revokes its whole family and fails with 401.</summary>
    Task<AuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken ct);

    /// <summary>Revokes the token's family. Succeeds even when the token is unknown.</summary>
    Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct);
}

/// <summary>Creates the short lived access JWT. Implemented in the Api project (the JWT libraries live there).</summary>
public interface IAccessTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) Issue(Guid participantId);
}

public enum AccessStatus
{
    /// <summary>No such participant, or revoked / deleted: the token is no longer good (401).</summary>
    Denied,

    /// <summary>Signed in, but notice or consent is missing / outdated (403 consent-required).</summary>
    NotOnboarded,

    Ok,
}

/// <summary>Checked against the database on every request, so a withdrawn consent takes effect at once.</summary>
public interface IParticipantAccess
{
    Task<AccessStatus> CheckAsync(Guid participantId, CancellationToken ct);
}

public sealed record ConsentInput(ConsentType Type, string Version, bool Granted);

public interface IParticipantService
{
    Task<ParticipantState> GetStateAsync(Guid participantId, ClientInfo client, CancellationToken ct);

    Task<ParticipantState> RecordConsentsAsync(
        Guid participantId,
        IReadOnlyList<ConsentInput> records,
        ClientInfo client,
        CancellationToken ct);

    Task<ParticipantState> SetNicknameAsync(Guid participantId, string? nickname, ClientInfo client, CancellationToken ct);

    /// <summary>Right to erasure: content is deleted for good, an anonymous trace stays (see ADR 004).</summary>
    Task DeleteAsync(Guid participantId, ClientInfo client, CancellationToken ct);
}

/// <summary>Reads the schema version table. Read-only: the application never changes the schema.</summary>
public interface ISchemaStatusProvider
{
    Task<SchemaStatus> GetAsync(CancellationToken ct);
}

public sealed record SchemaStatus(bool Ready, int Expected, int? Found, string? Message);
