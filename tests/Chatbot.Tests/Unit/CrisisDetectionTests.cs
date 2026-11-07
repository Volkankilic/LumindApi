using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Chatbot.Application.Chat;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Chat.Crisis;
using Chatbot.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Chatbot.Tests.Unit;

public class CrisisNormalizationTests
{
    private static string[] Tokens(string text, bool join = false) => [.. CrisisText.Tokenize(text, join).Tokens];

    [Theory]
    [InlineData("İSTİYORUM", "istiyorum")]
    [InlineData("ISITMA IŞIK", "isitma isik")]
    [InlineData("ıssız İstanbul", "isiz istanbul")]
    [InlineData("ÇŞĞÖÜ çşğöü", "csgou csgou")]
    [InlineData("ölmeekk istiyorrum", "olmek istiyorum")]
    [InlineData("aaaa", "a")]
    [InlineData("  çok   fazla\t\nboşluk  ", "cok fazla bosluk")]
    [InlineData("ölmek, istiyorum!", "olmek istiyorum")]
    public void TextIsLowercasedTheTurkishWay_AccentsFolded_AndRepeatedLettersSquashed(string input, string expected) =>
        Assert.Equal(expected, string.Join(' ', Tokens(input)));

    [Fact]
    public void DigitsAreNotSquashed_SoANumberStaysIntact()
    {
        Assert.Equal(["112"], Tokens("112"));
        Assert.Equal(["1112"], Tokens("1112"));
    }

    [Fact]
    public void LettersSpelledOutOneByOne_AreJoined_WhenAtLeastFourFollowEachOther()
    {
        var sep = CrisisText.Tokenize("ö l m e k istiyorum", false);
        Assert.Equal(["olmek", "istiyorum"], sep.Tokens);
        Assert.Equal(["olmek"], sep.Runs);

        Assert.Equal(["o", "l", "m"], Tokens("ö l m")); // three letters are not a spelled-out word
        Assert.Equal(["olmek"], Tokens("ö.l.m.e.k"));
        Assert.Equal(["olmek"], Tokens("ö.l.m.e.k", join: true));
    }

    [Fact]
    public void PunctuationEitherSeparatesOrJoinsWords_DependingOnTheView()
    {
        Assert.Equal(["yasamak", "istemiyorum"], Tokens("yaşamak.istemiyorum"));
        Assert.Equal(["yasamakistemiyorum"], Tokens("yaşamak.istemiyorum", join: true));
        Assert.Equal(["olmek"], Tokens("ö.lmek", join: true));
    }

    [Fact]
    public void InvisibleCharacters_FullWidthLetters_AndCombiningMarks_DoNotHideAWord()
    {
        Assert.Equal(["olmek"], Tokens("ö​lmek"));
        Assert.Equal(["olmek"], Tokens("ｏｌｍｅｋ"));
        Assert.Equal(["olmek"], Tokens("ölmek"));
        Assert.Equal(["olmek"], Tokens("ö­lmek")); // soft hyphen
    }

    [Fact]
    public void BrokenUnicode_NeverThrows()
    {
        var tokens = Tokens("\uD800 ölmek \uDC00\uDC00 istiyorum ￿");
        Assert.Contains("olmek", tokens);
        Assert.Empty(Tokens(string.Empty));
        Assert.Empty(Tokens("   "));
    }
}

public class CrisisLexiconTests
{
    private static string EmbeddedJson()
    {
        using var stream = typeof(CrisisLexicon).Assembly.GetManifestResourceStream(CrisisLexicon.ResourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheEmbeddedList_LoadsWithItsVersion()
    {
        var lexicon = CrisisLexicon.LoadEmbedded();
        Assert.Equal("0.1-taslak", lexicon.Version);
        Assert.InRange(lexicon.PhraseCount, 40, 200);
        Assert.Equal(lexicon.PhraseCount, lexicon.Phrases.Count);
        Assert.Equal("0.1-taslak", RuleCrisisDetector.CreateDefault().Version);
    }

    [Fact]
    public void EveryExample_IsFoundByItsOwnPhrase_WithTheRightCategory()
    {
        var root = JsonNode.Parse(EmbeddedJson())!.AsObject();
        var phrases = root["phrases"]!.AsArray();
        var categories = root["categories"]!.AsArray().ToDictionary(c => (string)c!["id"]!, c => Enum.Parse<CrisisCategory>((string)c!["name"]!));

        foreach (var phrase in phrases.Select(p => p!.AsObject()))
        {
            var alone = JsonNode.Parse(root.ToJsonString())!.AsObject();
            alone["phrases"] = new JsonArray(JsonNode.Parse(phrase.ToJsonString()));
            var detector = new RuleCrisisDetector(CrisisLexicon.Parse(alone.ToJsonString()));

            var id = (string)phrase["id"]!;
            var assessment = detector.Assess((string)phrase["example"]!);
            Assert.True(assessment.IsCrisis, $"the example of phrase {id} is not found by the phrase itself");
            Assert.Equal(categories[(string)phrase["category"]!], assessment.Category);
        }
    }

    [Fact]
    public void EveryPhraseHasAnExampleAReasonAndARiskNote_SoTheReviewSheetIsComplete()
    {
        foreach (var info in CrisisLexicon.LoadEmbedded().Phrases)
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Example), info.Id);
            Assert.False(string.IsNullOrWhiteSpace(info.Reason), info.Id);
            Assert.False(string.IsNullOrWhiteSpace(info.Risk), info.Id);
        }
    }

    [Fact]
    public void TheKeywordStandIn_Phrases_AreAllCoveredByTheRealDetector()
    {
        var detector = RuleCrisisDetector.CreateDefault();
        foreach (var text in new[] { "kendime zarar", "intihar", "yaşamak istemiyorum", "ölmek istiyorum" })
        {
            Assert.True(detector.Assess(text).IsCrisis, text);
        }
    }

    [Theory]
    [InlineData("""{"version":"","categories":[],"phrases":[]}""", "no version")]
    [InlineData("""{"version":"x","categories":[],"phrases":[]}""", "no phrases")]
    [InlineData("""{"version":"x","categories":[{"id":"A","name":"Nope"}],"phrases":[]}""", "unknown name")]
    [InlineData("""{"version":"x","categories":[{"id":"A","name":"DeathWish"}],"phrases":[{"id":"P1","category":"Z","pattern":"a","gap":0}]}""", "unknown category")]
    [InlineData("""{"version":"x","categories":[{"id":"A","name":"DeathWish"}],"phrases":[{"id":"P1","category":"A","pattern":"a","gap":9}]}""", "gap")]
    [InlineData("""{"version":"x","categories":[{"id":"A","name":"DeathWish"}],"phrases":[{"id":"P1","category":"A","pattern":"","gap":0}]}""", "no pattern")]
    [InlineData("""{"version":"x","categories":[{"id":"A","name":"DeathWish"}],"phrases":[{"id":"P1","category":"A","pattern":"a|","gap":0},{"id":"P1","category":"A","pattern":"b","gap":0}]}""", "duplicate")]
    [InlineData("""{"version":"x","categories":[{"id":"A","name":"DeathWish"}],"phrases":[{"id":"P1","category":"A","pattern":"*","gap":0}]}""", "empty word")]
    public void ABrokenList_IsRefused_AtStartup(string json, string _) =>
        Assert.Throws<InvalidOperationException>(() => CrisisLexicon.Parse(json));

    [Fact]
    public void AnErrorMessage_NamesThePhraseIdButNeverThePhrase()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CrisisLexicon.Parse(
            """{"version":"x","categories":[{"id":"A","name":"DeathWish"}],"phrases":[{"id":"P7","category":"A","pattern":"gizli*","gap":99}]}"""));
        Assert.Contains("P7", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("gizli", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePhrasesAreMatchedInOrder_AndOnlyWithinTheGap()
    {
        var detector = RuleCrisisDetector.CreateDefault();
        Assert.True(detector.Assess("ölmek istiyorum").IsCrisis);
        Assert.True(detector.Assess("ölmek artık çok istiyorum").IsCrisis); // two words in between: inside the gap of 3
        Assert.False(detector.Assess("istiyorum ölmek").IsCrisis); // wrong order
        Assert.False(detector.Assess("ölmek bir iki üç dört beş altı istiyorum").IsCrisis); // too far apart
    }

    [Fact]
    public void ThereIsNoNegationSuppression_InThisVersion_AndThatIsTheStatedLimit()
    {
        var detector = RuleCrisisDetector.CreateDefault();
        Assert.True(detector.Assess("İntihar etmeyi düşünmüyorum").IsCrisis); // ADR 008: recall first
        Assert.True(detector.Assess("Kendime zarar vermek istemiyorum").IsCrisis);
    }

    [Fact]
    public void DeathFear_AndOrdinaryOncologyTalk_AreNotFlagged()
    {
        var detector = RuleCrisisDetector.CreateDefault();
        foreach (var text in new[]
        {
            "Ölmekten korkuyorum", "Ölmek istemiyorum", "Ya ölürsem çocuklarım ne olur", "Kemoterapi beni mahvediyor", "Bu ağrı beni öldürüyor",
            "Kanser onu öldürdü", "Göğsümdeki ameliyat yeri ağrıyor", "Nefes darlığım var",
        })
        {
            Assert.False(detector.Assess(text).IsCrisis, text);
        }
    }
}

public class CrisisFailSafeTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " EXCEPTION:" + exception));
    }

    private sealed class Throwing(string message) : ICrisisDetector
    {
        public CrisisAssessment Assess(string text) => throw new InvalidOperationException(message + text);
    }

    [Fact]
    public void WhenTheDetectorThrows_TheMessageCountsAsACrisis_AndOnlyTheExceptionTypeIsLogged()
    {
        var logger = new ListLogger<FailSafeCrisisDetector>();
        var detector = new FailSafeCrisisDetector(new Throwing("SIR-MESAJ-"), logger);

        var assessment = detector.Assess("GIZLI-ICERIK-42");

        Assert.True(assessment.IsCrisis);
        Assert.Equal(CrisisCategory.Unknown, assessment.Category);
        var line = Assert.Single(logger.Lines);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.DoesNotContain("GIZLI-ICERIK-42", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SIR-MESAJ-", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompositeFlagsWhenAnyMemberDoes_AndIsProductionGradeOnlyWithTheRealOne()
    {
        var real = RuleCrisisDetector.CreateDefault();
        var composite = new CompositeCrisisDetector(new NoopCrisisDetector(), real);
        Assert.True(composite.Assess("ölmek istiyorum").IsCrisis);
        Assert.False(composite.Assess("bugün hava güzel").IsCrisis);
        Assert.True(composite.IsProductionGrade);
        Assert.False(new CompositeCrisisDetector(new NoopCrisisDetector(), new DevKeywordCrisisDetector()).IsProductionGrade);
    }

    [Fact]
    public void TheCategory_IsInMemoryOnly_NoOtherFileOfTheApplicationTouchesIt()
    {
        // ADR 008 section 6: the category is never stored, logged, audited, sent or shown. Only the files below may name it.
        var allowed = new[] { "Contracts.cs", "ChatService.cs", "Crisis.cs", "CrisisLexicon.cs", "RuleCrisisDetector.cs" };
        var src = Path.Combine(FindApi(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", ""))))
        {
            if (allowed.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("CrisisCategory", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CrisisAssessment", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheChatServiceNeverPassesTheCategoryOn_ExceptToTheReplyProvider()
    {
        var text = File.ReadAllText(Path.Combine(FindApi(), "src", "Chatbot.Application", "Chat", "ChatService.cs"));
        Assert.DoesNotContain("assessment.Category", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Category", text.Replace("CrisisAssessment", string.Empty), StringComparison.Ordinal);
    }

    private static string FindApi()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Chatbot.sln not found");
    }
}

/// <summary>
/// Measurements on the SYNTHETIC corpora (written by the authors of the phrase list). They show that the list does what
/// it was written to do and they guard against regressions; they are NOT the sensitivity on real patient language.
/// </summary>
public class CrisisMeasurementTests(ITestOutputHelper output)
{
    private static readonly RuleCrisisDetector Detector = RuleCrisisDetector.CreateDefault();

    private static bool Flags(string text) => Detector.Assess(text).IsCrisis;

    [Fact]
    public void Sensitivity_OnThePositiveCorpus_PerVariantAndPerCategory()
    {
        var positives = CrisisCorpus.Positive();
        var byVariant = new Dictionary<string, (int Hit, int Total)>();
        var byCategory = new Dictionary<string, (int Hit, int Total)>();
        var missed = new List<string>();

        foreach (var entry in positives)
        {
            foreach (var (name, text) in CrisisCorpus.Variants(entry.Text))
            {
                var hit = Flags(text);
                var v = byVariant.GetValueOrDefault(name);
                byVariant[name] = (v.Hit + (hit ? 1 : 0), v.Total + 1);
                var c = byCategory.GetValueOrDefault(entry.Tag);
                byCategory[entry.Tag] = (c.Hit + (hit ? 1 : 0), c.Total + 1);
                if (!hit)
                {
                    missed.Add($"[{entry.Tag}/{name}] {entry.Text}");
                }
            }
        }

        output.WriteLine($"Positive corpus: {positives.Count} sentences, {byVariant.Values.Sum(x => x.Total)} test points (10 variants each)");
        foreach (var (name, r) in byVariant)
        {
            output.WriteLine($"  variant {name,-18} {r.Hit}/{r.Total}  {100.0 * r.Hit / r.Total:F1}%");
        }

        foreach (var (name, r) in byCategory.OrderBy(k => k.Key))
        {
            output.WriteLine($"  category {name}: {r.Hit}/{r.Total}  {100.0 * r.Hit / r.Total:F1}%");
        }

        var total = byVariant.Values.Aggregate((a, b) => (a.Hit + b.Hit, a.Total + b.Total));
        output.WriteLine($"  ALL: {total.Hit}/{total.Total}  {100.0 * total.Hit / total.Total:F1}%");
        foreach (var m in missed)
        {
            output.WriteLine("  MISSED " + m);
        }

        // A regression floor, not a claim about real language.
        Assert.True(byVariant["original"].Hit >= byVariant["original"].Total * 0.97, "recall on the original sentences fell");
        Assert.True(byVariant["ascii"].Hit >= byVariant["ascii"].Total * 0.97, "recall on ASCII-only text fell");
        Assert.True(byVariant["upper"].Hit >= byVariant["upper"].Total * 0.97, "recall on upper case fell");
        Assert.True(byVariant["embedded"].Hit >= byVariant["embedded"].Total * 0.97, "recall inside long text fell");
    }

    [Fact]
    public void FalsePositives_OnTheNegativeCorpus_AreListedOneByOne()
    {
        var negatives = CrisisCorpus.Negative();
        var flagged = negatives.Where(n => Flags(n.Text)).ToList();

        output.WriteLine($"Negative corpus: {negatives.Count} sentences, {flagged.Count} flagged ({100.0 * flagged.Count / negatives.Count:F1}%)");
        foreach (var group in negatives.GroupBy(n => n.Tag))
        {
            var f = group.Count(n => Flags(n.Text));
            output.WriteLine($"  {group.Key,-6} {f}/{group.Count()}");
        }

        foreach (var f in flagged)
        {
            output.WriteLine($"  FALSE POSITIVE [{f.Tag}] {f.Text}");
        }

        // The ordinary chat and the symptom sentences must stay clean; the borderline metaphors are the accepted cost.
        Assert.DoesNotContain(flagged, f => f.Tag is "ORD" or "SYM" or "LIFE" or "GRIEF");
        Assert.True(flagged.Count <= negatives.Count * 0.15, "too many false positives");
    }

    [Fact]
    public void Sensitivity_OnTheHardIndirectSentences_IsMeasuredApart_AndIsLow()
    {
        var hard = CrisisCorpus.Hard();
        var hits = hard.Where(h => Flags(h.Text)).ToList();
        output.WriteLine($"Hard/indirect corpus: {hits.Count}/{hard.Count} found ({100.0 * hits.Count / hard.Count:F1}%)");
        foreach (var h in hard.Where(h => !Flags(h.Text)))
        {
            output.WriteLine($"  MISSED [{h.Tag}] {h.Text}");
        }

        foreach (var h in hits)
        {
            output.WriteLine($"  found  [{h.Tag}] {h.Text}");
        }

        Assert.True(hard.Count > 0);
    }

    [Fact]
    public void LongTextBuiltFromCleanSentences_DoesNotCreateAMatchAcrossTheJoints()
    {
        var clean = CrisisCorpus.Negative().Where(n => !Flags(n.Text)).Select(n => n.Text).ToList();
        var random = new Random(20260508);
        var flagged = new List<string>();
        for (var trial = 0; trial < 300; trial++)
        {
            var text = string.Join(' ', Enumerable.Range(0, 12).Select(_ => clean[random.Next(clean.Count)]));
            if (Flags(text))
            {
                flagged.Add(text);
            }
        }

        output.WriteLine($"Cross-sentence joins: {flagged.Count}/300 shuffled paragraphs of 12 clean sentences were flagged");
        foreach (var f in flagged.Take(5))
        {
            output.WriteLine("  " + f);
        }

        Assert.Empty(flagged);
    }

    [Fact]
    public void ArbitraryAndBrokenUnicode_NeverThrows()
    {
        var random = new Random(42);
        var pools = new (int Low, int High)[]
        {
            (0x20, 0x7E), (0xA0, 0xFF), (0x100, 0x17F), (0x300, 0x36F), (0x200B, 0x200F), (0xD800, 0xDFFF), (0x0, 0x1F), (0x4E00, 0x4E80),
            (0x0600, 0x06FF), (0xE000, 0xE0FF), (0xFE00, 0xFE0F), (0x2000, 0x206F), (0xFF00, 0xFFEF),
        };
        var words = new[] { "ölmek", "istiyorum", "intihar", "112", "kendime", "zarar", " ", ".", "ö.l.m.e.k", "a b c d e" };

        for (var i = 0; i < 4000; i++)
        {
            var length = random.Next(0, 3000);
            var text = new StringBuilder(length);
            while (text.Length < length)
            {
                if (random.Next(4) == 0)
                {
                    text.Append(words[random.Next(words.Length)]);
                }
                else
                {
                    var pool = pools[random.Next(pools.Length)];
                    text.Append((char)random.Next(pool.Low, pool.High + 1));
                }
            }

            _ = Detector.Assess(text.ToString()); // must not throw
        }

        foreach (var text in new[] { string.Empty, " ", "\0", new string('\uD800', 500), new string('́', 1000), "￿￾" })
        {
            _ = Detector.Assess(text);
        }
    }

    [Fact]
    public void AdversarialInputs_StayFast_BecauseTheMatcherIsLinear_NoBacktracking()
    {
        var inputs = new Dictionary<string, string>
        {
            ["'ölmek ' x 333"] = string.Concat(Enumerable.Repeat("ölmek ", 333)),
            ["'ölmek istiyorum ' x 125"] = string.Concat(Enumerable.Repeat("ölmek istiyorum ", 125)),
            ["'a ' x 1000"] = string.Concat(Enumerable.Repeat("a ", 1000)),
            ["'ö.l.m.e.k ' x 200"] = string.Concat(Enumerable.Repeat("ö.l.m.e.k ", 200)),
            ["'a' x 2000"] = new string('a', 2000),
            ["'.' x 2000"] = new string('.', 2000),
            ["' ' x 2000"] = new string(' ', 2000),
            ["'intihar ' x 250"] = string.Concat(Enumerable.Repeat("intihar ", 250)),
            ["'kendime ' x 250"] = string.Concat(Enumerable.Repeat("kendime ", 250)),
            ["single letters x 1000 (one spelled-out run)"] = string.Join(' ', Enumerable.Repeat("a", 1000)),
            ["'ölmey ' then 'istiyor' x 300"] = string.Concat(Enumerable.Repeat("ölmey istiyor ", 150)),
            ["100 000 characters (50 times the limit)"] = string.Concat(Enumerable.Repeat("ölmek bir iki istiyorum ", 4200)),
        };

        _ = Detector.Assess("ısınma turu"); // JIT warm-up
        foreach (var (name, text) in inputs)
        {
            var watch = Stopwatch.StartNew();
            _ = Detector.Assess(text);
            watch.Stop();
            output.WriteLine($"  {name,-48} {text.Length,7} chars  {watch.Elapsed.TotalMilliseconds,8:F2} ms");
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"'{name}' took {watch.Elapsed.TotalMilliseconds:F0} ms");
        }
    }

    [Fact]
    public void ATwoThousandCharacterMessage_IsAssessedInMilliseconds()
    {
        const string Paragraph = "Bugün hastanede uzun süre bekledik, doktor sonuçları gösterdi ve bir sonraki kontrol için randevu verdi. Eve dönünce biraz dinlendim, akşam çorba içtim. ";
        var ordinary = string.Concat(Enumerable.Repeat(Paragraph, 14))[..2000];
        var withCrisis = ordinary[..1960] + " Ölmek istiyorum.";
        var punctuated = string.Concat(Enumerable.Repeat("a.b,c;d ölmek! istiyor... ", 80))[..2000];

        foreach (var (name, text) in new[] { ("ordinary 2000 chars", ordinary), ("crisis phrase at the end", withCrisis), ("heavy punctuation", punctuated) })
        {
            // Warm up until the JIT has promoted the hot path to optimised code (tiered compilation does it after ~100 ms).
            for (var i = 0; i < 500; i++)
            {
                _ = Detector.Assess(text);
            }

            Thread.Sleep(300);
            for (var i = 0; i < 500; i++)
            {
                _ = Detector.Assess(text);
            }

            const int Runs = 3000;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < Runs; i++)
            {
                _ = Detector.Assess(text);
            }

            watch.Stop();
            var micros = watch.Elapsed.TotalMilliseconds * 1000 / Runs;
            output.WriteLine($"  {name,-26} {text.Length} chars: {micros:F1} µs per message ({micros / 1000:F3} ms)");
            Assert.True(micros < 5000, $"{name} took {micros:F0} µs");
        }
    }
}
