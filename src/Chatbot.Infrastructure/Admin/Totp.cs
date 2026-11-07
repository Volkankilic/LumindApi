using System.Security.Cryptography;
using System.Text;

namespace Chatbot.Infrastructure.Admin;

/// <summary>RFC 4648 Base32 (upper case, no padding on output), the alphabet authenticator apps expect for a TOTP secret.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    /// <summary>Spaces, hyphens, padding and case are ignored. Returns null for any other character.</summary>
    public static byte[]? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw) || raw is '-' or '=')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}

/// <summary>
/// TOTP, RFC 6238 (HMAC-SHA-1, 30 second steps, 6 digits) over HOTP, RFC 4226. No dependency: the algorithm is a few lines
/// and the RFC test vectors are in the tests. The window (the previous, the current and the next step) allows clock drift;
/// a code is accepted only for a step AFTER the last accepted one (replay protection, kept by the caller).
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20;
    public const int Window = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code of one time step. <paramref name="digits"/> is 6 in the application and 8 in the RFC 6238 test vectors.</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step the code belongs to (within the window around <paramref name="now"/>) and is newer than
    /// <paramref name="lastAcceptedStep"/>, or null. Every candidate step is compared in constant time.
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastAcceptedStep)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepOf(now);
        var codeBytes = Encoding.ASCII.GetBytes(code);
        long? match = null;
        for (var step = current - Window; step <= current + Window; step++)
        {
            var same = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, step)), codeBytes);
            if (same && step > lastAcceptedStep)
            {
                match = step;
            }
        }

        return match;
    }

    /// <summary>The otpauth:// URI an authenticator app reads. Shown ONLY in the terminal, once. The issuer and account are not secret.</summary>
    public static string Uri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}"
        + $"?secret={base32Secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}
