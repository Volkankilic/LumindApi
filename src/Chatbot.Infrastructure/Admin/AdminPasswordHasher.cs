using System.Security.Cryptography;
using System.Text;
using Chatbot.Domain.Rules;

namespace Chatbot.Infrastructure.Admin;

/// <summary>
/// PBKDF2-HMAC-SHA512 with a random 16 byte salt and a 64 byte output (ADR 009). The stored text is versioned:
/// <c>pbkdf2-sha512$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c> (Base64), so the cost can be raised later; an older hash is
/// recognised by <see cref="NeedsRehash"/> and rewritten after a correct sign-in. Never logs or returns the password.
/// </summary>
public sealed class AdminPasswordHasher(int iterations)
{
    public const string Prefix = "pbkdf2-sha512";
    private const int SaltBytes = 16;
    private const int HashBytes = 64;

    public int Iterations { get; } = iterations;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time comparison. A malformed stored value is simply "false" (after doing comparable work).</summary>
    public bool Verify(string password, string? stored)
    {
        if (!TryParse(stored, out var iterationCount, out var salt, out var expected))
        {
            // Same work as a real check, so an unknown user or a broken value costs the same time.
            _ = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), new byte[SaltBytes], Iterations, HashAlgorithmName.SHA512, HashBytes);
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterationCount, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string? stored) =>
        !TryParse(stored, out var iterationCount, out _, out _) || iterationCount < Iterations;

    private static bool TryParse(string? stored, out int iterationCount, out byte[] salt, out byte[] hash)
    {
        iterationCount = 0;
        salt = [];
        hash = [];
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Prefix || !int.TryParse(parts[1], out iterationCount) || iterationCount < 1)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length > 0;
    }

    /// <summary>At least the configured length, and not the user name (case-insensitive). Nothing else is guessed.</summary>
    public static string? PasswordProblem(string? password, string username, int minLength)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
        {
            return $"The password must have at least {minLength} characters.";
        }

        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
        {
            return "The password must differ from the user name.";
        }

        return null;
    }
}

/// <summary>Recovery codes: 10 characters from the participant code alphabet, shown as "ABCDE-FGHJK"; only a keyed hash is stored.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    public const int Length = 10;

    public static string New()
    {
        var chars = new char[Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = ParticipantCode.Alphabet[RandomNumberGenerator.GetInt32(ParticipantCode.Alphabet.Length)];
        }

        return new string(chars);
    }

    public static string Display(string code) => $"{code[..(Length / 2)]}-{code[(Length / 2)..]}";

    /// <summary>Upper case, without spaces and hyphens; null when it cannot be a recovery code.</summary>
    public static string? Normalize(string? raw)
    {
        var normalized = ParticipantCode.Normalize(raw);
        return normalized.Length == Length && normalized.All(c => ParticipantCode.Alphabet.Contains(c, StringComparison.Ordinal))
            ? normalized
            : null;
    }
}

/// <summary>
/// The lock of an account after repeated failures: 1, 2, 4, 8 … minutes, never longer than the configured maximum.
/// Pure arithmetic so it can be tested without a clock.
/// </summary>
public static class AdminLockSchedule
{
    /// <summary>Zero (no lock) below the threshold; from the threshold on it doubles with every further failure.</summary>
    public static TimeSpan LockFor(int consecutiveFailures, int lockAfterFailures, int maxMinutes)
    {
        if (consecutiveFailures < lockAfterFailures)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Min(consecutiveFailures - lockAfterFailures, 20);
        var minutes = Math.Min(Math.Pow(2, exponent), maxMinutes);
        return TimeSpan.FromMinutes(minutes);
    }
}
