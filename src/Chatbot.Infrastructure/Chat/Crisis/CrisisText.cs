using System.Globalization;
using System.Text;

namespace Chatbot.Infrastructure.Chat.Crisis;

/// <summary>
/// Turns a message into comparable tokens. This is for MATCHING ONLY: the stored message is never changed.
/// Steps: Unicode compatibility form (NFKD, so full-width letters and accents unfold), marks and invisible format
/// characters dropped (a zero-width space inside a word does not hide it), lower case in the Turkish way (I, İ, ı, i all
/// become "i"), accent folding (ç ş ğ ö ü → c s g o u, so text typed without Turkish letters is found too), runs of
/// the same letter squashed to one ("ölmeekk" → "olmek"), everything else a separator.
/// The same function is applied to the phrase list, so both sides always agree.
/// </summary>
public static class CrisisText
{
    /// <summary>Spelled-out letters ("ö l m e k", "ö.l.m.e.k") are joined when this many single letters follow each other.</summary>
    public const int MinSpelledOutRun = 4;

    /// <param name="Tokens">The words, in order. A spelled-out run is already joined into one word.</param>
    /// <param name="Runs">The joined spelled-out runs (they are also checked against the phrases with the spaces removed).</param>
    public sealed record Tokenized(List<string> Tokens, List<string> Runs);

    /// <summary>
    /// <paramref name="punctuationJoins"/> false: punctuation separates words ("yaşamak.istemiyorum" → two words).
    /// true: punctuation disappears ("ö.lmek" → "olmek"). The detector tries both views.
    /// </summary>
    public static Tokenized Tokenize(string text, bool punctuationJoins)
    {
        var folded = Fold(text, punctuationJoins);
        var raw = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var tokens = new List<string>(raw.Length);
        List<string>? runs = null;
        var i = 0;
        while (i < raw.Length)
        {
            if (IsSingleLetter(raw[i]))
            {
                var end = i;
                while (end < raw.Length && IsSingleLetter(raw[end]))
                {
                    end++;
                }

                if (end - i >= MinSpelledOutRun)
                {
                    var joined = string.Join(string.Empty, raw, i, end - i);
                    tokens.Add(joined);
                    (runs ??= []).Add(joined);
                    i = end;
                    continue;
                }
            }

            tokens.Add(raw[i]);
            i++;
        }

        return new Tokenized(tokens, runs ?? []);
    }

    /// <summary>One word of the phrase list in the same form (no spaces, no punctuation).</summary>
    public static string NormalizeWord(string word) => Fold(word, punctuationJoins: true).Trim();

    private static bool IsSingleLetter(string token) => token.Length == 1 && char.IsLetter(token[0]);

    private static string Fold(string text, bool punctuationJoins)
    {
        var source = Decompose(text);
        var result = new StringBuilder(source.Length);
        var previousLetter = '\0';

        foreach (var c in source)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.Format:
                    break; // accents and invisible characters: not part of the word, and they do not split it

                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                    var letter = FoldLetter(c);
                    if (letter != previousLetter)
                    {
                        result.Append(letter);
                        previousLetter = letter;
                    }

                    break;

                case UnicodeCategory.DecimalDigitNumber:
                    result.Append(c);
                    previousLetter = '\0';
                    break;

                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Control:
                    AppendSpace(result);
                    previousLetter = '\0';
                    break;

                default: // punctuation, symbols, emoji (surrogates) and the like
                    if (!punctuationJoins)
                    {
                        AppendSpace(result);
                        previousLetter = '\0';
                    }

                    break;
            }
        }

        return result.ToString();
    }

    private static void AppendSpace(StringBuilder result)
    {
        if (result.Length > 0 && result[^1] != ' ')
        {
            result.Append(' ');
        }
    }

    /// <summary>NFKD; a string with a broken surrogate cannot be normalised, then it is used as it is (never an exception).</summary>
    private static string Decompose(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static char FoldLetter(char c) => c switch
    {
        'İ' or 'I' or 'ı' or 'î' or 'Î' => 'i',
        'Ç' or 'ç' => 'c',
        'Ş' or 'ş' => 's',
        'Ğ' or 'ğ' => 'g',
        'Ö' or 'ö' => 'o',
        'Ü' or 'ü' or 'û' or 'Û' => 'u',
        'â' or 'Â' => 'a',
        _ => char.ToLowerInvariant(c),
    };
}
