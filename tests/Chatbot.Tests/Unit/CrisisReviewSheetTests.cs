using System.Text;
using Chatbot.Infrastructure.Chat.Crisis;

namespace Chatbot.Tests.Unit;

/// <summary>
/// The expert review sheet (docs/kriz/ifade-listesi-inceleme.md) is generated from the very file the detector loads, so
/// the sheet can never drift from the list. The table sits between two markers; the rest of the file is hand-written.
/// After changing crisis-phrases.tr.json run once with UPDATE_CRISIS_REVIEW=1 to rewrite the table.
/// </summary>
public class CrisisReviewSheetTests
{
    private const string Begin = "<!-- BEGIN GENERATED TABLE: crisis-phrases.tr.json -->";
    private const string End = "<!-- END GENERATED TABLE -->";

    private static string SheetPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        var api = dir?.FullName ?? throw new InvalidOperationException("Chatbot.sln not found");
        return Path.GetFullPath(Path.Combine(api, "..", "docs", "kriz", "ifade-listesi-inceleme.md"));
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    public static string BuildTable(CrisisLexicon lexicon)
    {
        var table = new StringBuilder();
        table.AppendLine($"Liste sürümü: **{lexicon.Version}** · {lexicon.PhraseCount} kalıp · `api/src/Chatbot.Infrastructure/Chat/Crisis/crisis-phrases.tr.json`");
        table.AppendLine();
        table.AppendLine("| Kimlik | Kategori | İfade / kalıp (okunur biçim) | Örnek cümle | Gerekçe | Olası yanlış pozitif riski | Uzman kararı (Kabul / Değiştir / Çıkar) |");
        table.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var p in lexicon.Phrases)
        {
            var gap = p.Gap == 0 ? "bitişik" : $"en çok {p.Gap} sözcük arayla";
            table.AppendLine(
                $"| {p.Id} | {p.CategoryId} — {Cell(p.CategoryTitle)} | {Cell(p.Pattern)} ({gap}) | {Cell(p.Example)} | {Cell(p.Reason)} | {Cell(p.Risk)} |  |");
        }

        return table.ToString().TrimEnd();
    }

    [Fact]
    public void TheReviewTable_IsTheOneGeneratedFromTheCurrentList()
    {
        var path = SheetPath();
        var expected = BuildTable(CrisisLexicon.LoadEmbedded());
        var content = File.ReadAllText(path, Encoding.UTF8);
        var start = content.IndexOf(Begin, StringComparison.Ordinal);
        var stop = content.IndexOf(End, StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start, "the generated-table markers are missing from the review sheet");

        var updated = content[..(start + Begin.Length)] + "\n\n" + expected + "\n\n" + content[stop..];
        if (Environment.GetEnvironmentVariable("UPDATE_CRISIS_REVIEW") == "1")
        {
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return;
        }

        Assert.True(
            string.Equals(content, updated, StringComparison.Ordinal),
            "docs/kriz/ifade-listesi-inceleme.md is out of step with crisis-phrases.tr.json: run the tests once with UPDATE_CRISIS_REVIEW=1.");
    }
}
