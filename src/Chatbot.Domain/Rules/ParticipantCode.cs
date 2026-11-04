using System.Text;

namespace Chatbot.Domain.Rules;

/// <summary>Participant code format. Mirrors client/src/app/core/validation/participant-code.ts.</summary>
public static class ParticipantCode
{
    /// <summary>No 0/O or 1/I/L: too easy to mix up when copying a code by hand.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int Length = 8;

    /// <summary>Upper-cases (culture independent) and drops whitespace and hyphens. Other characters are kept.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    public static bool IsValid(string normalized) =>
        normalized.Length == Length && normalized.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}
