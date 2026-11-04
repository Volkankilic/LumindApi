using System.Text.RegularExpressions;
using Chatbot.Application.Chat;

namespace Chatbot.Infrastructure.Privacy;

/// <summary>
/// Rule-based masking of personal identifiers, for text that is about to leave for a MODEL PROVIDER (phase 6 connects it;
/// today no model exists). It never changes what is stored. Placeholders: [E-POSTA], [BAGLANTI], [IBAN], [TC], [TELEFON],
/// [KART], [TARIH].
///
/// LIMITS (written down in docs/kvkk/veri-envanteri.md and the pre-pilot checklist): names of people and places, and indirect
/// identifiers in free text ("the nurse at the oncology ward of X hospital"), CANNOT be caught reliably with rules. This is a
/// first line of defence for structured identifiers, not anonymisation. A full date is masked even when it is not a birth
/// date (an appointment, say): a false positive is accepted, a leak is not.
/// </summary>
public sealed partial class RulePiiRedactor : IPiiRedactor
{
    /// <summary>What a text becomes when a pattern takes too long (fail closed: nothing is passed on).</summary>
    public const string Withheld = "[METIN MASKELENDI]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            var result = Email().Replace(text, "[E-POSTA]");
            result = Url().Replace(result, "[BAGLANTI]");
            result = Iban().Replace(result, m => IsValidIban(m.Value) ? "[IBAN]" : m.Value);
            result = Tckn().Replace(result, m => IsValidTckn(m.Value) ? "[TC]" : m.Value);
            result = Phone().Replace(result, "[TELEFON]");
            result = LongNumber().Replace(result, "[KART]");
            result = DateNumeric().Replace(result, "[TARIH]");
            result = DateIso().Replace(result, "[TARIH]");
            result = DateWords().Replace(result, "[TARIH]");
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    // ---- validation helpers (public for the tests)

    /// <summary>The Turkish national id number checksum (11 digits, first one not 0).</summary>
    public static bool IsValidTckn(string value)
    {
        if (value.Length != 11 || value[0] == '0' || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var d = value.Select(c => c - '0').ToArray();
        var tenth = (((d[0] + d[2] + d[4] + d[6] + d[8]) * 7) - (d[1] + d[3] + d[5] + d[7])) % 10;
        if (tenth < 0)
        {
            tenth += 10;
        }

        return d[9] == tenth && d.Take(10).Sum() % 10 == d[10];
    }

    /// <summary>IBAN mod 97 check (spaces ignored).</summary>
    public static bool IsValidIban(string value)
    {
        var iban = new string([.. value.Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();
        if (iban.Length != 26 || !iban.StartsWith("TR", StringComparison.Ordinal))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var piece = char.IsAsciiDigit(c) ? (c - '0').ToString() : (c - 'A' + 10).ToString();
            foreach (var digit in piece)
            {
                remainder = ((remainder * 10) + (digit - '0')) % 97;
            }
        }

        return remainder == 1;
    }

    // ---- patterns

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex Url();

    // TR + 2 check digits + 20 digits, spaces allowed between groups of four.
    [GeneratedRegex(@"(?<![A-Za-z0-9])TR\s?\d{2}(?:\s?\d{4}){5}\s?\d{2}(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<!\d)[1-9]\d{10}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Tckn();

    // Mobile: 5xx xxx xx xx with an optional 0 / +90 / 0090 in front. Landline: needs the 0, +90 or brackets, so that
    // an ordinary ten digit number is not taken for a phone number.
    [GeneratedRegex(
        @"(?<![\d])(?:(?:\+|00)90[ .\-]?)?\(?(?:0[ ]?)?\(?5\d{2}\)?[ .\-]?\d{3}[ .\-]?\d{2}[ .\-]?\d{2}(?!\d)"
        + @"|(?<![\d])(?:(?:\+|00)90[ .\-]?\(?|\(?0[ ]?\(?)[2-4]\d{2}\)?[ .\-]?\d{3}[ .\-]?\d{2}[ .\-]?\d{2}(?!\d)"
        + @"|(?<![\d])\([2-4]\d{2}\)[ .\-]?\d{3}[ .\-]?\d{2}[ .\-]?\d{2}(?!\d)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Phone();

    // Card-like numbers, by their real layouts, so that a date or another number written right after a card is not swallowed:
    // 4-6-5 (15 digits), 4-4-4-4 (16 digits, spaces or hyphens optional), or 13-19 digits in one run.
    [GeneratedRegex(
        @"(?<!\d)(?:\d{4}[ \-]?\d{6}[ \-]?\d{5}|\d{4}(?:[ \-]?\d{4}){3}|\d{13,19})(?!\d)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex LongNumber();

    // 31.12.1985, 1/2/1985, 05-03-1985
    [GeneratedRegex(@"(?<!\d)(?:0?[1-9]|[12]\d|3[01])[./\-](?:0?[1-9]|1[0-2])[./\-](?:19|20)\d{2}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DateNumeric();

    // 1985-03-12
    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DateIso();

    // 12 Mart 1985
    [GeneratedRegex(
        @"(?<!\d)(?:0?[1-9]|[12]\d|3[01])\s+(?:ocak|şubat|mart|nisan|mayıs|haziran|temmuz|ağustos|eylül|ekim|kasım|aralık)\s+(?:19|20)\d{2}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex DateWords();
}
