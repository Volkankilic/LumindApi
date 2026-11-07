using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chatbot.Tests.TestSupport;

/// <summary>
/// The synthetic crisis corpora (tests/Chatbot.Tests/CrisisCorpus/*). They are written by the same people who wrote the
/// phrase list, so the numbers they give are NOT the sensitivity on real patient language (docs/kriz/kriz-algilama.md).
/// </summary>
internal static partial class CrisisCorpus
{
    public sealed record Entry(string Tag, string Text);

    private static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Chatbot.sln not found"), "tests", "Chatbot.Tests", "CrisisCorpus");
    }

    private static List<Entry> Read(string file) =>
        [.. File.ReadAllLines(Path.Combine(Dir(), file), Encoding.UTF8)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('|', 2))
            .Select(p => new Entry(p[0], p[1]))];

    public static List<Entry> Positive() => Read("positive.tsv");

    public static List<Entry> Hard() => Read("positive-hard.tsv");

    public static List<Entry> Negative() => Read("negative.txt");

    private static readonly CultureInfo Turkish = new("tr-TR");

    private const string NeutralBefore =
        "Bu hafta çok şey yaşadım, hastane, evrak ve yorgunluk arasında gidip geldim. Sabah erken kalkıp ilaçlarımı aldım ve kısa bir yürüyüş yaptım. " +
        "Akşam komşum uğradı, biraz sohbet ettik, çay içtik. Kızım yarın arayacağını söyledi. ";

    private const string NeutralAfter =
        " Sonra televizyon izledim, yarın için birkaç not aldım. Gece biraz erken yatmayı düşünüyorum, sabah randevum var.";

    public static IEnumerable<(string Name, string Text)> Variants(string text)
    {
        yield return ("original", text);
        yield return ("ascii", Ascii(text));
        yield return ("upper", text.ToUpper(Turkish));
        yield return ("upper-ascii", Ascii(text).ToUpperInvariant());
        yield return ("repeated-letters", Repeat(text));
        yield return ("dotted-word", Spell(text, "."));
        yield return ("spaced-word", Spell(text, " "));
        yield return ("glued-with-dots", text.Replace(' ', '.'));
        yield return ("embedded", NeutralBefore + text + NeutralAfter);
        yield return ("embedded-long", string.Concat(Enumerable.Repeat(NeutralBefore, 4)) + text + string.Concat(Enumerable.Repeat(NeutralAfter, 3)));
    }

    public static string Ascii(string text)
    {
        var map = new Dictionary<char, char>
        {
            ['ç'] = 'c', ['Ç'] = 'C', ['ş'] = 's', ['Ş'] = 'S', ['ğ'] = 'g', ['Ğ'] = 'G', ['ı'] = 'i', ['İ'] = 'I', ['ö'] = 'o', ['Ö'] = 'O', ['ü'] = 'u', ['Ü'] = 'U',
        };
        return string.Concat(text.Select(c => map.TryGetValue(c, out var m) ? m : c));
    }

    private static string Repeat(string text) => Words().Replace(text, m =>
    {
        var w = m.Value;
        return w.Length < 4 ? w : w[..3] + w[2] + w[3..] + w[^1] + w[^1];
    });

    /// <summary>Writes the longest word letter by letter ("ö.l.m.e.k" / "ö l m e k").</summary>
    private static string Spell(string text, string separator)
    {
        var longest = Words().Matches(text).OrderByDescending(m => m.Length).FirstOrDefault();
        if (longest is null || longest.Length < 5)
        {
            return text;
        }

        return text[..longest.Index] + string.Join(separator, longest.Value.Select(c => c.ToString())) + text[(longest.Index + longest.Length)..];
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();
}
