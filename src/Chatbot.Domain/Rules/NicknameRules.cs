using System.Text.RegularExpressions;

namespace Chatbot.Domain.Rules;

public enum NicknameProblem { TooShort, TooLong, Personal, InvalidChars }

/// <summary>Nickname validation. Mirrors client/src/app/core/validation/nickname.ts.</summary>
public static partial class NicknameRules
{
    public const int MinLength = 2;
    public const int MaxLength = 20;

    public static NicknameProblem? Validate(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        var length = value.EnumerateRunes().Count();
        if (length < MinLength)
        {
            return NicknameProblem.TooShort;
        }

        if (length > MaxLength)
        {
            return NicknameProblem.TooLong;
        }

        if (value.Contains('@', StringComparison.Ordinal) || SevenDigits().IsMatch(value))
        {
            return NicknameProblem.Personal;
        }

        return AllowedChars().IsMatch(value) ? null : NicknameProblem.InvalidChars;
    }

    /// <summary>The stored form: trimmed, like the client sends it.</summary>
    public static string Clean(string raw) => raw.Trim();

    [GeneratedRegex(@"\d{7,}")]
    private static partial Regex SevenDigits();

    [GeneratedRegex(@"^[\p{L}\p{N} _-]+$")]
    private static partial Regex AllowedChars();
}
