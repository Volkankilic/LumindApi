using System.Security.Cryptography;
using Chatbot.Application.Abstractions;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Admin;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Dev;

/// <summary>What a test needs to sign in as a synthetic admin. Returned ONLY to the test that asked, never stored or logged.</summary>
public sealed record CreatedTestAdmin(Guid AdminId, string Username, string Password, string TotpSecretBase32, string RecoveryCode);

/// <summary>
/// Development ONLY (registered in Development only). Creates and removes synthetic admins for the end-to-end tests: the user name
/// starts with `itest-`, and everything this service deletes is restricted to such admins, so a real account can never be touched.
/// The TOTP code is computed by the TEST from the returned secret; there is no way to skip TOTP.
/// </summary>
public sealed class DevTestAdminService(AppDbContext db, ISecretHasher hasher, AdminPasswordHasher passwords, TimeProvider clock)
{
    public const string UsernamePrefix = "itest-";

    public static bool IsTestAdmin(string? username) =>
        username is not null && username.StartsWith(UsernamePrefix, StringComparison.Ordinal) && username.Length > UsernamePrefix.Length;

    public async Task<CreatedTestAdmin> CreateAsync(CancellationToken ct)
    {
        var username = UsernamePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
        var password = "T-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var secret = Totp.NewSecret();
        var recovery = RecoveryCodes.New();
        var admin = new AdminUser
        {
            Id = Guid.CreateVersion7(),
            Username = username,
            PasswordHash = passwords.Hash(password),
            TotpSecret = Base32.Encode(secret),
            TotpEnabled = true,
            Status = AdminStatus.Active,
            CreatedAt = clock.GetUtcNow(),
        };
        db.AdminUsers.Add(admin);
        db.AdminRecoveryCodes.Add(new AdminRecoveryCode { Id = Guid.CreateVersion7(), AdminId = admin.Id, CodeHash = hasher.HashCode(recovery) });
        await db.SaveChangesAsync(ct);
        return new CreatedTestAdmin(admin.Id, username, password, Base32.Encode(secret), recovery);
    }

    public async Task DeleteAsync(Guid adminId, CancellationToken ct)
    {
        var admin = await db.AdminUsers.AsNoTracking().Where(a => a.Id == adminId).Select(a => new { a.Id, a.Username }).FirstOrDefaultAsync(ct);
        if (admin is null)
        {
            return;
        }

        if (!IsTestAdmin(admin.Username))
        {
            throw new InvalidOperationException("Refusing to delete an admin whose user name does not start with 'itest-'.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.CrisisAcknowledgements.Where(a => a.AdminId == adminId).ExecuteDeleteAsync(ct);
        await db.AuditLogs.Where(a => a.ActorId == adminId || a.TargetId == adminId).ExecuteDeleteAsync(ct);
        await db.AdminUsers.Where(a => a.Id == adminId && a.Username.StartsWith(UsernamePrefix)).ExecuteDeleteAsync(ct); // tokens and recovery codes cascade
        await tx.CommitAsync(ct);
    }
}
