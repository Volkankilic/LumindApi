namespace Chatbot.Domain.Entities;

/// <summary>
/// A research team member who may use the admin panel (never a participant). Created only from the command line.
/// The password is stored as a salted PBKDF2 hash; the TOTP secret is encrypted (Phase 4 field encryption).
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; }

    /// <summary>Lower case, unique. Not a real name by design (e.g. "arastirmaci-1").</summary>
    public required string Username { get; set; }

    /// <summary>Versioned PBKDF2 string, e.g. "pbkdf2-sha512$310000$salt$hash".</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Encrypted at rest ("enc:v1:…"); the plain value is the Base32 secret. Null until enrollment.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>False until the first code was confirmed during <c>create-admin</c>. An admin without TOTP cannot sign in.</summary>
    public bool TotpEnabled { get; set; }

    /// <summary>The time step (RFC 6238) of the last accepted code: a code of the same or an older step is a replay.</summary>
    public long TotpLastStep { get; set; }

    public AdminStatus Status { get; set; }

    public int FailedAttempts { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

public enum AdminStatus { Active, Disabled }
