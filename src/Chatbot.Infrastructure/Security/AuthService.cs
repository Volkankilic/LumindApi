using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Auth;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Participants;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Security;

public sealed class AuthService(
    AppDbContext db,
    ISecretHasher hasher,
    IAccessTokenIssuer accessTokens,
    IAuditWriter audit,
    AuthOptions options,
    AuthFailureBudget failureBudget,
    ILogger<AuthService> logger,
    TimeProvider clock) : IAuthService
{
    // Compared against when no participant has the code, so a wrong code costs the same work as a right one.
    private static readonly byte[] DummyHash = new byte[32];

    public async Task<AuthResult> SignInAsync(string? code, ClientInfo client, CancellationToken ct)
    {
        var normalized = ParticipantCode.Normalize(code);
        var formatOk = ParticipantCode.IsValid(normalized);
        var hash = hasher.HashCode(normalized); // always computed, even for a malformed code

        var participant = await db.Participants.FirstOrDefaultAsync(p => p.CodeHash == hash, ct);
        var stored = participant is null ? DummyHash : Encoding.UTF8.GetBytes(participant.CodeHash);
        var sameHash = CryptographicOperations.FixedTimeEquals(stored, Encoding.UTF8.GetBytes(hash));

        // Malformed, unknown, revoked and deleted codes are one and the same answer.
        if (!formatOk || participant is null || !sameHash
            || participant.Status is ParticipantStatus.Revoked or ParticipantStatus.Deleted)
        {
            await audit.WriteAsync(ActorType.Anonymous, null, AuditActions.AuthCodeFailed, null, null, client.Ip, ct);
            if (failureBudget.RecordFailure(clock.GetUtcNow()))
            {
                // Only a warning and an audit row: nobody is locked out (see AuthFailureBudget). No IP, no code.
                logger.LogWarning(
                    "Failed code attempts across all clients reached {Threshold} within {Minutes} minutes: possible guessing attack.",
                    options.FailureBudgetThreshold,
                    options.FailureBudgetWindowMinutes);
                await audit.WriteAsync(ActorType.System, null, AuditActions.AuthFailureBudgetExceeded, null, null, null, ct);
            }

            throw AppException.InvalidCode();
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // One active session: a new sign-in ends every earlier one.
        await db.RefreshTokens
            .Where(t => t.ParticipantId == participant.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        if (participant.Status == ParticipantStatus.Invited)
        {
            participant.Status = ParticipantStatus.Active;
        }

        participant.LastSeenAt = now;

        var familyExpiresAt = now.AddDays(options.RefreshTokenMaxDays);
        var issued = AddRefreshToken(participant.Id, Guid.CreateVersion7(), now, familyExpiresAt);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await audit.WriteAsync(ActorType.Participant, participant.Id, AuditActions.AuthCodeSucceeded, null, null, client.Ip, ct);
        return await ResultAsync(participant, issued, ct);
    }

    public async Task<AuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw AppException.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var tokenHash = hasher.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw AppException.Unauthorized();

        var state = RefreshTokenRules.Evaluate(token, now);
        if (state != RefreshTokenState.Valid)
        {
            if (state == RefreshTokenState.Reused)
            {
                // A spent token came back: treat the whole sign-in as compromised.
                await RevokeFamilyAsync(token.FamilyId, now, ct);
                await audit.WriteAsync(ActorType.Participant, token.ParticipantId, AuditActions.AuthRefreshReuseDetected, null, null, client.Ip, ct);
            }

            throw AppException.Unauthorized();
        }

        var participant = await db.Participants.FirstOrDefaultAsync(p => p.Id == token.ParticipantId, ct);
        if (participant is null || participant.Status != ParticipantStatus.Active)
        {
            await RevokeFamilyAsync(token.FamilyId, now, ct);
            throw AppException.Unauthorized();
        }

        // Claim the old token first. Two requests with the same token cannot both succeed.
        var replacementId = Guid.CreateVersion7();
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByTokenId, replacementId), ct);
        if (claimed != 1)
        {
            await RevokeFamilyAsync(token.FamilyId, now, ct);
            throw AppException.Unauthorized();
        }

        participant.LastSeenAt = now;
        var issued = AddRefreshToken(participant.Id, token.FamilyId, now, token.FamilyExpiresAt, replacementId);
        await db.SaveChangesAsync(ct);

        return await ResultAsync(participant, issued, ct);
    }

    public async Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = hasher.HashRefreshToken(refreshToken);
        var token = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (token is null)
        {
            return;
        }

        await RevokeFamilyAsync(token.FamilyId, clock.GetUtcNow(), ct);
        await audit.WriteAsync(ActorType.Participant, token.ParticipantId, AuditActions.AuthLogout, null, null, client.Ip, ct);
    }

    private IssuedRefreshToken AddRefreshToken(
        Guid participantId,
        Guid familyId,
        DateTimeOffset now,
        DateTimeOffset familyExpiresAt,
        Guid? id = null)
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = RefreshTokenRules.NextExpiry(now, options.RefreshTokenDays, familyExpiresAt);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = id ?? Guid.CreateVersion7(),
            ParticipantId = participantId,
            TokenHash = hasher.HashRefreshToken(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            FamilyExpiresAt = familyExpiresAt,
        });
        return new IssuedRefreshToken(raw, expiresAt);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private async Task<AuthResult> ResultAsync(Participant participant, IssuedRefreshToken refresh, CancellationToken ct)
    {
        var (token, expiresAt) = accessTokens.Issue(participant.Id);
        var state = await ParticipantStates.LoadStateAsync(db, participant, ct);
        var expiresIn = Math.Max(1, (int)(expiresAt - clock.GetUtcNow()).TotalSeconds);
        return new AuthResult(token, expiresIn, state, refresh);
    }
}
