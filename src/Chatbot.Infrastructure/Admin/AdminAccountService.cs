using System.Text.RegularExpressions;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Admin;

/// <summary>An admin that is prepared but NOT stored: it is stored only after the first TOTP code was confirmed.</summary>
public sealed record PendingAdmin(string Username, string PasswordHash, string SecretBase32, string OtpauthUri, IReadOnlyList<string> RecoveryCodes);

/// <summary>A TOTP reset that is prepared but NOT applied: nothing changes until the first code of the NEW secret is confirmed.</summary>
public sealed record PendingTotpReset(Guid AdminId, string Username, string SecretBase32, string OtpauthUri, IReadOnlyList<string> RecoveryCodes);

public enum AdminAccountProblem { None, BadUsername, UsernameTaken, WeakPassword, WrongCode, NotFound }

/// <summary>
/// The account operations of the command line (create-admin, set-admin-password, enable/disable-admin). The panel has none of
/// them (ADR 009). Secrets (password, TOTP secret, recovery codes) pass through memory and the terminal only.
/// </summary>
public sealed partial class AdminAccountService(
    AppDbContext db,
    ISecretHasher hasher,
    IAuditWriter audit,
    AdminPasswordHasher passwords,
    AdminAuthOptions options,
    TimeProvider clock)
{
    public const string Issuer = "Lumind";

    [GeneratedRegex(@"^[a-z][a-z0-9._-]{2,31}$")]
    private static partial Regex UsernamePattern();

    public static string NormalizeUsername(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    public async Task<(AdminAccountProblem Problem, PendingAdmin? Pending)> PrepareAsync(string? username, string? password, CancellationToken ct)
    {
        var name = NormalizeUsername(username);
        if (!UsernamePattern().IsMatch(name))
        {
            return (AdminAccountProblem.BadUsername, null);
        }

        if (await db.AdminUsers.AsNoTracking().AnyAsync(a => a.Username == name, ct))
        {
            return (AdminAccountProblem.UsernameTaken, null);
        }

        if (AdminPasswordHasher.PasswordProblem(password, name, options.PasswordMinLength) is not null)
        {
            return (AdminAccountProblem.WeakPassword, null);
        }

        var secret = Totp.NewSecret();
        var base32 = Base32.Encode(secret);
        var codes = Enumerable.Range(0, RecoveryCodes.Count).Select(_ => RecoveryCodes.New()).ToList();
        return (AdminAccountProblem.None, new PendingAdmin(
            name,
            passwords.Hash(password!),
            base32,
            Totp.Uri(Issuer, name, base32),
            [.. codes.Select(RecoveryCodes.Display)]));
    }

    /// <summary>Checks the first code of the authenticator app; only then the account (active, TOTP on) and its recovery codes are stored.</summary>
    public async Task<AdminAccountProblem> ConfirmAndStoreAsync(PendingAdmin pending, string? firstCode, CancellationToken ct)
    {
        var secret = Base32.Decode(pending.SecretBase32)!;
        var now = clock.GetUtcNow();
        var step = Totp.Verify(secret, firstCode?.Trim(), now, 0);
        if (step is null)
        {
            return AdminAccountProblem.WrongCode;
        }

        var admin = new AdminUser
        {
            Id = Guid.CreateVersion7(),
            Username = pending.Username,
            PasswordHash = pending.PasswordHash,
            TotpSecret = pending.SecretBase32,
            TotpEnabled = true,
            TotpLastStep = step.Value, // the confirming code cannot be used again to sign in
            Status = AdminStatus.Active,
            CreatedAt = now,
        };
        db.AdminUsers.Add(admin);
        foreach (var display in pending.RecoveryCodes)
        {
            db.AdminRecoveryCodes.Add(new AdminRecoveryCode
            {
                Id = Guid.CreateVersion7(),
                AdminId = admin.Id,
                CodeHash = hasher.HashCode(RecoveryCodes.Normalize(display)!),
            });
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(ActorType.System, null, AuditActions.AdminAccountCreated, nameof(AdminUser), admin.Id, null, ct);
        return AdminAccountProblem.None;
    }

    public async Task<AdminAccountProblem> SetPasswordAsync(string? username, string? password, CancellationToken ct)
    {
        var name = NormalizeUsername(username);
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == name, ct);
        if (admin is null)
        {
            return AdminAccountProblem.NotFound;
        }

        if (AdminPasswordHasher.PasswordProblem(password, name, options.PasswordMinLength) is not null)
        {
            return AdminAccountProblem.WeakPassword;
        }

        admin.PasswordHash = passwords.Hash(password!);
        admin.FailedAttempts = 0;
        admin.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        await RevokeSessionsAsync(admin.Id, ct); // a changed password ends every session
        await audit.WriteAsync(ActorType.System, null, AuditActions.AdminPasswordChanged, nameof(AdminUser), admin.Id, null, ct);
        return AdminAccountProblem.None;
    }

    public async Task<AdminAccountProblem> SetStatusAsync(string? username, AdminStatus status, CancellationToken ct)
    {
        var name = NormalizeUsername(username);
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == name, ct);
        if (admin is null)
        {
            return AdminAccountProblem.NotFound;
        }

        admin.Status = status;
        if (status == AdminStatus.Active)
        {
            admin.FailedAttempts = 0;
            admin.LockedUntil = null;
        }

        await db.SaveChangesAsync(ct);
        if (status == AdminStatus.Disabled)
        {
            await RevokeSessionsAsync(admin.Id, ct); // a disabled account's open sessions close at once
        }

        var action = status == AdminStatus.Disabled ? AuditActions.AdminAccountDisabled : AuditActions.AdminAccountEnabled;
        await audit.WriteAsync(ActorType.System, null, action, nameof(AdminUser), admin.Id, null, ct);
        return AdminAccountProblem.None;
    }

    /// <summary>
    /// Step one of "lost phone": a NEW secret and NEW recovery codes are made, but nothing is stored or changed (the old TOTP and the old
    /// recovery codes keep working until the new secret is confirmed).
    /// </summary>
    public async Task<(AdminAccountProblem Problem, PendingTotpReset? Pending)> PrepareTotpResetAsync(string? username, CancellationToken ct)
    {
        var name = NormalizeUsername(username);
        var admin = await db.AdminUsers.AsNoTracking().Where(a => a.Username == name).Select(a => new { a.Id, a.Username }).FirstOrDefaultAsync(ct);
        if (admin is null)
        {
            return (AdminAccountProblem.NotFound, null);
        }

        var base32 = Base32.Encode(Totp.NewSecret());
        var codes = Enumerable.Range(0, RecoveryCodes.Count).Select(_ => RecoveryCodes.New()).ToList();
        return (AdminAccountProblem.None, new PendingTotpReset(admin.Id, admin.Username, base32, Totp.Uri(Issuer, admin.Username, base32), [.. codes.Select(RecoveryCodes.Display)]));
    }

    /// <summary>
    /// Step two: checks the first code of the new secret, then in ONE save: replaces the secret, replaces ALL recovery codes (the old ones
    /// stop working), clears the lock and closes every session. A wrong code changes nothing.
    /// </summary>
    public async Task<AdminAccountProblem> ConfirmTotpResetAsync(PendingTotpReset pending, string? firstCode, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var step = Totp.Verify(Base32.Decode(pending.SecretBase32)!, firstCode?.Trim(), now, 0);
        if (step is null)
        {
            return AdminAccountProblem.WrongCode;
        }

        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == pending.AdminId, ct);
        if (admin is null)
        {
            return AdminAccountProblem.NotFound;
        }

        admin.TotpSecret = pending.SecretBase32;
        admin.TotpEnabled = true;
        admin.TotpLastStep = step.Value; // the confirming code cannot sign in
        admin.FailedAttempts = 0;
        admin.LockedUntil = null;

        db.AdminRecoveryCodes.RemoveRange(await db.AdminRecoveryCodes.Where(c => c.AdminId == admin.Id).ToListAsync(ct));
        foreach (var display in pending.RecoveryCodes)
        {
            db.AdminRecoveryCodes.Add(new AdminRecoveryCode
            {
                Id = Guid.CreateVersion7(),
                AdminId = admin.Id,
                CodeHash = hasher.HashCode(RecoveryCodes.Normalize(display)!),
            });
        }

        foreach (var token in await db.AdminRefreshTokens.Where(t => t.AdminId == admin.Id && t.RevokedAt == null).ToListAsync(ct))
        {
            token.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(ActorType.System, null, AuditActions.AdminTotpReset, nameof(AdminUser), admin.Id, null, ct);
        return AdminAccountProblem.None;
    }

    public Task<int> CountActiveAsync(CancellationToken ct) =>
        db.AdminUsers.AsNoTracking().CountAsync(a => a.Status == AdminStatus.Active && a.TotpEnabled, ct);

    private async Task RevokeSessionsAsync(Guid adminId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var open = await db.AdminRefreshTokens.Where(t => t.AdminId == adminId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in open)
        {
            token.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
