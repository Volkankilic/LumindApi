using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Admin;
using Chatbot.Application.Auth;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Admin;

/// <summary>
/// The lock schedule for user names that do NOT exist, kept in memory. Without it a locked real account would answer
/// differently from an unknown name, and the difference would tell an attacker which names exist (ADR 009). Real accounts
/// keep their counters in the database; this mirrors them with the same schedule. One API instance is assumed.
/// </summary>
public sealed class AdminLoginTracker(TimeProvider clock)
{
    private const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil, DateTimeOffset LastSeen)> _entries = new();

    public TimeSpan RemainingLock(string username)
    {
        if (_entries.TryGetValue(username, out var entry) && entry.LockedUntil > clock.GetUtcNow())
        {
            return entry.LockedUntil - clock.GetUtcNow();
        }

        return TimeSpan.Zero;
    }

    /// <summary>Counts one failure and returns the lock that now applies (zero below the threshold).</summary>
    public TimeSpan RecordFailure(string username, int lockAfterFailures, int maxMinutes)
    {
        var now = clock.GetUtcNow();
        if (_entries.Count > MaxEntries)
        {
            foreach (var stale in _entries.Where(e => now - e.Value.LastSeen > TimeSpan.FromHours(1)).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(stale, out _);
            }
        }

        var updated = _entries.AddOrUpdate(
            username,
            _ => Build(1, now, lockAfterFailures, maxMinutes),
            (_, old) => Build(old.Failures + 1, now, lockAfterFailures, maxMinutes));
        return updated.LockedUntil > now ? updated.LockedUntil - now : TimeSpan.Zero;
    }

    private static (int, DateTimeOffset, DateTimeOffset) Build(int failures, DateTimeOffset now, int lockAfter, int maxMinutes) =>
        (failures, now + AdminLockSchedule.LockFor(failures, lockAfter, maxMinutes), now);
}

/// <summary>
/// Admin sign-in in two steps (password, then TOTP or a recovery code), refresh with rotation and reuse detection, logout.
/// Every failure of the first step is the same <c>invalid-credentials</c>; a lock is a 429 that looks the same for a real and an
/// unknown name. Never logs a password, a TOTP code, a recovery code or a token.
/// </summary>
public sealed class AdminAuthService(
    AppDbContext db,
    ISecretHasher hasher,
    IAdminTokenIssuer tokens,
    IAuditWriter audit,
    AdminAuthOptions options,
    AdminPasswordHasher passwords,
    AdminLoginTracker tracker,
    ILogger<AdminAuthService> logger,
    TimeProvider clock) : IAdminAuthService
{
    public const int MaxFieldLength = 256;

    public async Task<AdminLoginResult> LoginAsync(string? username, string? password, ClientInfo client, CancellationToken ct)
    {
        var name = NormalizeName(username);
        var admin = name.Length == 0 ? null : await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == name, ct);

        // Always the same amount of work, for a real name, an unknown one and a broken hash.
        var passwordOk = passwords.Verify(Bound(password), admin?.PasswordHash);
        var now = clock.GetUtcNow();

        var remaining = admin is null ? tracker.RemainingLock(name) : RemainingLock(admin, now);
        if (remaining > TimeSpan.Zero)
        {
            throw AppException.RateLimited(Seconds(remaining));
        }

        if (!passwordOk || admin is null || admin.Status != AdminStatus.Active || !admin.TotpEnabled)
        {
            throw await RegisterFailureAsync(admin, name, AuditActions.AdminLoginFailed, client, ct);
        }

        if (passwords.NeedsRehash(admin.PasswordHash))
        {
            admin.PasswordHash = passwords.Hash(Bound(password));
            await db.SaveChangesAsync(ct);
        }

        var (token, expiresAt) = tokens.IssueMfa(admin.Id);
        return new AdminLoginResult(token, Seconds(expiresAt - now));
    }

    public async Task<AdminAuthResult> CompleteAsync(string? mfaToken, string? code, string? recoveryCode, ClientInfo client, CancellationToken ct)
    {
        var adminId = await tokens.ValidateMfaAsync(mfaToken) ?? throw AppException.InvalidCredentials();
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == adminId, ct);
        var now = clock.GetUtcNow();
        if (admin is null || admin.Status != AdminStatus.Active || !admin.TotpEnabled)
        {
            throw AppException.InvalidCredentials();
        }

        var remaining = RemainingLock(admin, now);
        if (remaining > TimeSpan.Zero)
        {
            throw AppException.RateLimited(Seconds(remaining));
        }

        var usedRecovery = false;
        var accepted = false;
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            accepted = usedRecovery = await ConsumeRecoveryCodeAsync(admin.Id, recoveryCode, now, ct);
        }
        else if (!string.IsNullOrWhiteSpace(code))
        {
            accepted = await AcceptTotpAsync(admin, code, now, ct);
        }

        if (!accepted)
        {
            throw await RegisterFailureAsync(admin, admin.Username, AuditActions.AdminTotpFailed, client, ct);
        }

        admin.FailedAttempts = 0;
        admin.LockedUntil = null;
        admin.LastLoginAt = now;
        var familyId = Guid.CreateVersion7();
        var familyExpiresAt = now.AddHours(options.AbsoluteSessionHours);
        var issued = AddRefreshToken(admin.Id, familyId, now, familyExpiresAt);
        await db.SaveChangesAsync(ct);

        if (usedRecovery)
        {
            await audit.WriteAsync(ActorType.Researcher, admin.Id, AuditActions.AdminRecoveryCodeUsed, nameof(AdminUser), admin.Id, client.Ip, ct);
        }

        await audit.WriteAsync(ActorType.Researcher, admin.Id, AuditActions.AdminLoginSucceeded, nameof(AdminUser), admin.Id, client.Ip, ct);
        return Result(admin, familyId, issued, now);
    }

    public async Task<AdminAuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw AppException.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var tokenHash = hasher.HashRefreshToken(refreshToken);
        var token = await db.AdminRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw AppException.Unauthorized();

        var state = RefreshTokenRules.Evaluate(token, now);
        if (state != RefreshTokenState.Valid)
        {
            if (state == RefreshTokenState.Reused)
            {
                // A spent token came back: treat the whole session as compromised.
                await RevokeFamilyAsync(token.FamilyId, now, ct);
                await audit.WriteAsync(ActorType.Researcher, token.AdminId, AuditActions.AdminRefreshReuseDetected, nameof(AdminUser), token.AdminId, client.Ip, ct);
            }

            throw AppException.Unauthorized();
        }

        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == token.AdminId, ct);
        if (admin is null || admin.Status != AdminStatus.Active || !admin.TotpEnabled)
        {
            await RevokeFamilyAsync(token.FamilyId, now, ct);
            throw AppException.Unauthorized();
        }

        // Claim the old token first: two requests with the same token cannot both succeed.
        var replacementId = Guid.CreateVersion7();
        var claimed = await db.AdminRefreshTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByTokenId, replacementId), ct);
        if (claimed != 1)
        {
            await RevokeFamilyAsync(token.FamilyId, now, ct);
            throw AppException.Unauthorized();
        }

        var issued = AddRefreshToken(admin.Id, token.FamilyId, now, token.FamilyExpiresAt, replacementId);
        await db.SaveChangesAsync(ct);
        return Result(admin, token.FamilyId, issued, now);
    }

    public async Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = hasher.HashRefreshToken(refreshToken);
        var token = await db.AdminRefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (token is null)
        {
            return;
        }

        await RevokeFamilyAsync(token.FamilyId, clock.GetUtcNow(), ct);
        await audit.WriteAsync(ActorType.Researcher, token.AdminId, AuditActions.AdminLogout, nameof(AdminUser), token.AdminId, client.Ip, ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<bool> AcceptTotpAsync(AdminUser admin, string code, DateTimeOffset now, CancellationToken ct)
    {
        var secret = Base32.Decode(admin.TotpSecret);
        if (secret is null)
        {
            return false;
        }

        var step = Totp.Verify(secret, code.Trim(), now, admin.TotpLastStep);
        if (step is null)
        {
            return false;
        }

        // Claim the step atomically: the same code cannot be used by two requests.
        var claimed = await db.AdminUsers
            .Where(a => a.Id == admin.Id && a.TotpLastStep < step.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TotpLastStep, step.Value), ct);
        if (claimed != 1)
        {
            return false;
        }

        admin.TotpLastStep = step.Value;
        db.Entry(admin).Property(a => a.TotpLastStep).IsModified = false; // already written; do not write it again
        return true;
    }

    private async Task<bool> ConsumeRecoveryCodeAsync(Guid adminId, string raw, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = RecoveryCodes.Normalize(raw);
        if (normalized is null)
        {
            return false;
        }

        var hash = hasher.HashCode(normalized);
        var claimed = await db.AdminRecoveryCodes
            .Where(c => c.AdminId == adminId && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        return claimed == 1;
    }

    /// <summary>Counts a failure (database for a real account, memory for an unknown name), locks when due, audits, and returns what to throw.</summary>
    private async Task<AppException> RegisterFailureAsync(AdminUser? admin, string name, string action, ClientInfo client, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        TimeSpan locked;
        if (admin is null)
        {
            locked = tracker.RecordFailure(name, options.LockAfterFailures, options.LockMaxMinutes);
            await audit.WriteAsync(ActorType.Anonymous, null, action, null, null, client.Ip, ct);
        }
        else
        {
            admin.FailedAttempts++;
            locked = AdminLockSchedule.LockFor(admin.FailedAttempts, options.LockAfterFailures, options.LockMaxMinutes);
            if (locked > TimeSpan.Zero)
            {
                admin.LockedUntil = now + locked;
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ActorType.Researcher, admin.Id, action, nameof(AdminUser), admin.Id, client.Ip, ct);
            if (locked > TimeSpan.Zero)
            {
                logger.LogWarning("An admin account was locked after repeated failures (for {Minutes} minutes).", (int)Math.Ceiling(locked.TotalMinutes));
                await audit.WriteAsync(ActorType.Researcher, admin.Id, AuditActions.AdminAccountLocked, nameof(AdminUser), admin.Id, client.Ip, ct);
            }
        }

        // The lock itself is answered from the NEXT attempt on; this attempt is an ordinary failure.
        return AppException.InvalidCredentials();
    }

    private static TimeSpan RemainingLock(AdminUser admin, DateTimeOffset now) =>
        admin.LockedUntil is { } until && until > now ? until - now : TimeSpan.Zero;

    private AdminRefreshTokenIssue AddRefreshToken(Guid adminId, Guid familyId, DateTimeOffset now, DateTimeOffset familyExpiresAt, Guid? id = null)
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = RefreshTokenRules.NextExpiryMinutes(now, options.IdleMinutes, familyExpiresAt);
        db.AdminRefreshTokens.Add(new AdminRefreshToken
        {
            Id = id ?? Guid.CreateVersion7(),
            AdminId = adminId,
            TokenHash = hasher.HashRefreshToken(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            FamilyExpiresAt = familyExpiresAt,
        });
        return new AdminRefreshTokenIssue(raw, expiresAt);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.AdminRefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private AdminAuthResult Result(AdminUser admin, Guid familyId, AdminRefreshTokenIssue refresh, DateTimeOffset now)
    {
        var (token, expiresAt) = tokens.IssueAccess(admin.Id, familyId);
        return new AdminAuthResult(token, Seconds(expiresAt - now), admin.Username, new IssuedRefreshToken(refresh.Token, refresh.ExpiresAt));
    }

    private static int Seconds(TimeSpan span) => Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));

    private static string NormalizeName(string? username)
    {
        var name = (username ?? string.Empty).Trim().ToLowerInvariant();
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>A very long input is cut: the hash has to stay cheap to compute for whatever the client sends.</summary>
    private static string Bound(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length > MaxFieldLength ? value[..MaxFieldLength] : value;

    private sealed record AdminRefreshTokenIssue(string Token, DateTimeOffset ExpiresAt);
}

/// <summary>Per request: an active account with TOTP, and a session (family) with at least one live refresh token.</summary>
public sealed class AdminAccess(AppDbContext db, TimeProvider clock) : IAdminAccess
{
    public async Task<bool> CheckAsync(Guid adminId, Guid familyId, CancellationToken ct)
    {
        var active = await db.AdminUsers.AsNoTracking()
            .AnyAsync(a => a.Id == adminId && a.Status == AdminStatus.Active && a.TotpEnabled, ct);
        if (!active)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        return await db.AdminRefreshTokens.AsNoTracking()
            .AnyAsync(t => t.AdminId == adminId && t.FamilyId == familyId && t.RevokedAt == null && t.ExpiresAt > now && t.FamilyExpiresAt > now, ct);
    }

    public Task<string?> UsernameAsync(Guid adminId, CancellationToken ct) =>
        db.AdminUsers.AsNoTracking().Where(a => a.Id == adminId).Select(a => (string?)a.Username).FirstOrDefaultAsync(ct);
}
