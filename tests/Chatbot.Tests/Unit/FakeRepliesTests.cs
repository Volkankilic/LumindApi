using System.Text.RegularExpressions;
using Chatbot.Infrastructure.Chat;

namespace Chatbot.Tests.Unit;

// Ported from client/src/app/core/chat/mock-replies.spec.ts: the same checks run on the server's copy of the replies.

// docs/system-prompt.md, section 6 (language) and section 2 (never do).
public partial class FakeRepliesRuleTests
{
    private static readonly string[] ForbiddenPhrases =
    [
        "her şey düzelecek", "merak etme", "sadece pozitif düşün", "yapmalısın", "yapmalisin", "hissetmemelisin", "zorundasın",
    ];

    // Section 2: no diagnosis, treatment, drugs, lab results, prognosis, food, exercise.
    private static readonly string[] MedicalKeywords =
    [
        "ilaç", "ilac", "doz", "teşhis", "tanı", "tedavi öner", "tedavi et", "yaşam süresi", "prognoz", "ömrün", "kemoterapi",
        "ameliyat", "ışın", "beslen", "diyet", "egzersiz", "spor yap", "vitamin", "laboratuvar", "biyopsi", " evre", "sonuçlar",
    ];

    public static TheoryData<string> Ids => new(FakeReplies.Templates.Select(t => t.Id));

    private static string Text(string id) => FakeReplies.Templates.Single(t => t.Id == id).Text;

    private static string Lower(string id) => TurkishText.Lower(Text(id));

    [Fact]
    public void HasAtLeastSixTemplates_OnePerSituation()
    {
        Assert.True(FakeReplies.Templates.Count >= 6);
        var ids = FakeReplies.Templates.Select(t => t.Id).ToList();
        foreach (var id in new[] { "fear", "anger", "lonely", "breathing", "short" })
        {
            Assert.Contains(id, ids);
        }

        Assert.Contains(ids, id => id.StartsWith("general", StringComparison.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void ContainsNoForbiddenPhrase(string id)
    {
        foreach (var phrase in ForbiddenPhrases)
        {
            Assert.DoesNotContain(phrase, Lower(id), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void ContainsNoMedicalContent(string id)
    {
        foreach (var word in MedicalKeywords)
        {
            Assert.DoesNotContain(word, Lower(id), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void FollowsTheFourStepStructure_AtLeastFourParts(string id)
    {
        // The "general-2" reply is the shortest and has exactly four sentences in the client too.
        var sentences = SentenceSplit().Split(Text(id)).Where(s => s.Trim().Length > 3).ToList();
        Assert.True(sentences.Count >= 4, $"{id} has {sentences.Count} parts");
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void EndsWithAnInvitation_NotAnOrder(string id) =>
        Assert.Matches("istersen|istediğin|tercih", Lower(id));

    [Theory]
    [MemberData(nameof(Ids))]
    public void IsPlainText_NoHtmlOrMarkdown(string id) =>
        Assert.DoesNotMatch(@"[<>*_`#]|\]\(", Text(id));

    [Theory]
    [MemberData(nameof(Ids))]
    public void IsNotBossy_NoImperativeCommands(string id) =>
        // \p{L}: "doğal." must not match "al."
        Assert.DoesNotMatch(@"(?<!\p{L})(yap|et|git|al|başla|dur)\s*[.!]", Lower(id));

    [Fact]
    public void TheBreathingReply_OnlyOffersASimpleBreathingAndGroundingPrompt()
    {
        var breathing = Lower("breathing");
        Assert.Contains("nefes", breathing, StringComparison.Ordinal);
        Assert.Matches("adlandır|çevrende", breathing);
    }

    [Fact]
    public void TheCheckersReallyBite()
    {
        Assert.Contains("merak etme", TurkishText.Lower("Merak etme, her şey düzelecek."), StringComparison.Ordinal);
        Assert.Contains("ilaç", TurkishText.Lower("Bu ilaç sana iyi gelir."), StringComparison.Ordinal);
        Assert.Matches(@"(?<!\p{L})(yap|et)\s*[.!]", TurkishText.Lower("Şunu yap."));
        Assert.DoesNotMatch(@"(?<!\p{L})(al)\s*[.!]", TurkishText.Lower("Bu çok doğal."));
    }

    [GeneratedRegex(@"[.!?;]\s|[.!?]$")]
    private static partial Regex SentenceSplit();
}

public class FakeRepliesPickTests
{
    [Theory]
    [InlineData("Çok korkuyorum", "fear")]
    [InlineData("Bugün çok endişeliyim, uyuyamıyorum", "fear")]
    [InlineData("Her şeye öfkeliyim ve çok kızgınım", "anger")]
    [InlineData("Kendimi çok yalnız hissediyorum, ailemle de konuşamıyorum", "lonely")]
    [InlineData("Biraz nefes almak istiyorum, sakinleşmek için", "breathing")]
    [InlineData("Biraz sakinleşmek için nefes egzersizi yapmak istiyorum.", "breathing")]
    public void ChoosesByKeywordCategory(string text, string expected) =>
        Assert.Equal(expected, FakeReplies.Pick(text, 0).Id);

    [Theory]
    [InlineData("Merhaba", 0)]
    [InlineData("  ok ", 3)]
    public void AVeryShortMessageGetsTheShortReply(string text, int turn) =>
        Assert.Equal("short", FakeReplies.Pick(text, turn).Id);

    [Fact]
    public void OtherwiseTheGeneralRepliesAlternate()
    {
        const string text = "Bugün gün boyu pek bir şey yapamadım ve aklım dağınıktı";
        var first = FakeReplies.Pick(text, 0).Id;
        var second = FakeReplies.Pick(text, 1).Id;
        Assert.StartsWith("general", first, StringComparison.Ordinal);
        Assert.StartsWith("general", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Equal(first, FakeReplies.Pick(text, 2).Id);
    }

    [Theory]
    [InlineData("KORKUYORUM", "fear")]
    [InlineData("ÖFKELİYİM", "anger")]
    public void UnderstandsTurkishCapitalLetters(string text, string expected) =>
        Assert.Equal(expected, FakeReplies.Pick(text, 0).Id);
}

public class CrisisTextTests
{
    [Theory]
    [InlineData("Bugün kendime zarar vermeyi düşünüyorum")]
    [InlineData("intihar etmek geliyor aklıma")]
    [InlineData("artık yaşamak istemiyorum")]
    [InlineData("ölmek istiyorum")]
    [InlineData("KENDİME ZARAR vermek istiyorum")]
    public void TheDevDetector_RecognisesTheFewListedPhrases(string text) =>
        Assert.True(new DevKeywordCrisisDetector().IsCrisis(text));

    [Fact]
    public void TheDevDetector_IgnoresOrdinaryText() =>
        Assert.False(new DevKeywordCrisisDetector().IsCrisis("Bugün çok yorgunum"));

    [Fact]
    public void TheNoopDetector_NeverFlagsAnything()
    {
        var detector = new NoopCrisisDetector();
        Assert.False(detector.IsCrisis("intihar"));
        Assert.False(detector.IsCrisis("kendime zarar"));
    }

    [Fact]
    public void TheSafeReply_FollowsTheCrisisProtocol_EmpathySafetyProfessionalHelp112()
    {
        var text = new CrisisResponseProvider().GetReply();
        Assert.Contains("Güvende olman çok önemli", text, StringComparison.Ordinal);
        Assert.Contains("sağlık profesyoneline başvur", text, StringComparison.Ordinal);
        Assert.Contains("112", text, StringComparison.Ordinal);
        Assert.DoesNotContain("merak etme", TurkishText.Lower(text), StringComparison.Ordinal);
    }
}
