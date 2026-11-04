using System.Globalization;

namespace Chatbot.Infrastructure.Chat;

// Canned replies of the FAKE assistant (there is no AI in this phase). They are MOVED, not rewritten, from the
// client mock (client/src/app/core/chat/mock-replies.ts); the selection logic is the same.
//
// Every reply follows the 4-step structure of docs/system-prompt.md, section 9:
//   1. reflect the feeling  2. normalize  3. empower  4. invite / offer a choice
// and the rules of sections 2 and 6: no medical information, diagnosis, drugs, food or exercise advice,
// no prognosis, no empty reassurance, nothing bossy. Unit tests check these rules; keep them green when editing.
//
// TODO(volkan): the clinical team to review and approve all reply texts. The real assistant tone is defined
// with the clinical team before the pilot.

public sealed record ReplyTemplate(string Id, IReadOnlyList<string> Keywords, string Text);

public static class FakeReplies
{
    private const int ShortMessageLength = 12;

    public static IReadOnlyList<ReplyTemplate> Templates { get; } =
    [
        new(
            "fear",
            ["korku", "korkuyorum", "kaygı", "endişe", "panik", "tedirgin"],
            "Korkunun ve kaygının seni sardığını duyuyorum; bu gerçekten yorucu olabilir. Belirsizlik karşısında böyle hissetmek çok anlaşılır ve pek çok kişi benzer duyguları yaşar. Bunu ifade edebilmen, duygularınla temas kurabildiğini gösteriyor. İstersen bu korkunun sana en çok ne zaman geldiğini anlatabilirsin, ya da önce biraz sakinleşmek için birlikte yavaş bir nefes alıp vermeyi deneyebiliriz."),
        new(
            "anger",
            ["öfke", "kızgın", "sinir", "adaletsiz", "isyan"],
            "İçinde bir öfke biriktiğini hissediyorum; bu durumun adaletsiz görünmesi çok anlaşılır. Öfke, çoğu zaman önemsediğimiz bir şeyin zorlandığını gösteren doğal bir tepkidir ve böyle hissetmen yanlış değil. Bunu açıkça söyleyebilmen de bir güç. İstersen öfkenin nereye dokunduğunu birlikte anlamaya çalışabiliriz; ya da sadece dinlememi tercih edersen bunu da yapabilirim."),
        new(
            "lonely",
            ["yalnız", "anlaşılmı", "aile", "eşim", "çocuk", "annem", "babam"],
            "Kendini yalnız ya da anlaşılmamış hissettiğini duyuyorum; bu ağır bir his olabilir. Yakınların olsa bile bazı şeyleri paylaşmak zor gelebilir ve bu pek çok kişinin yaşadığı bir durum. Burada bunu dile getirmen, bağ kurma isteğini gösteriyor. İstersen yakınlarınla aranda neyin zor geldiğini ya da en çok neyi paylaşamadığını konuşabiliriz."),
        new(
            "breathing",
            ["nefes", "sakinleş", "rahatla", "gevşe"],
            "Biraz sakinleşmek istediğini duyuyorum ve bunu fark etmen çok değerli. Gerginlik hissettiğimizde bedenin hızlanması doğal bir tepkidir. İstersen şöyle deneyebiliriz: rahat bir pozisyonda burnundan yavaşça dört saymaya kadar nefes alıp, altı saymaya kadar usulca verebilirsin. Ardından çevrende gördüğün üç şeyi adlandırmak da seni şimdiye getirebilir. Nasıl hissettirdiğini anlatırsan memnun olurum; istediğin an bırakabilirsin."),
        new(
            "short",
            [],
            "Buradayım ve seni dinliyorum. Kısa bir şey yazmış olman da tamamen normal; bazen kelimeler zor gelir. İstediğin hızda ilerleyebiliriz, acele yok. İstersen bugün nasıl hissettiğini bir iki kelimeyle anlatabilirsin ya da sadece sessizce burada kalabilirsin."),
        new(
            "general-1",
            [],
            "Paylaştığın için teşekkür ederim; bunları yazmak kolay olmayabilir. Böyle bir dönemde duyguların hızla değişebilir ve bu çok doğal. Bunları anlatabilmen, kendine dürüst olabildiğini ve güçlü yanların olduğunu gösteriyor. İstersen şu an en çok neyin ağırlık yaptığını birlikte biraz daha konuşabiliriz, ya da başka bir yerden başlayabiliriz."),
        new(
            "general-2",
            [],
            "Anlattıkların, yoğun bir iç dünyada ilerlediğini düşündürüyor. Bu süreçte karışık duyguların aynı anda var olması gerçekten çok yaygın. Kendi hızında ilerlemene alan açabildiğin için de emeğini görüyorum. İstersen bugün aklından geçenlerden birini seçip onunla devam edebiliriz."),
    ];

    public static IReadOnlyList<ReplyTemplate> GeneralTemplates { get; } =
        Templates.Where(t => t.Id.StartsWith("general", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Picks a template: first by keyword category, a very short message gets the "short" reply, otherwise the
    /// general replies in turn (<paramref name="turn"/> counts the general replies given so far).
    /// </summary>
    public static ReplyTemplate Pick(string text, int turn)
    {
        var normalized = TurkishText.Lower(text);
        foreach (var template in Templates)
        {
            if (template.Keywords.Any(k => normalized.Contains(k, StringComparison.Ordinal)))
            {
                return template;
            }
        }

        if (normalized.Trim().Length < ShortMessageLength)
        {
            return Templates.First(t => t.Id == "short");
        }

        return GeneralTemplates[turn % GeneralTemplates.Count];
    }
}

/// <summary>Turkish aware lower casing that does not depend on ICU or the host culture (İ -> i, I -> ı).</summary>
public static class TurkishText
{
    public static string Lower(string text) =>
        text.Replace('İ', 'i').Replace('I', 'ı').ToLower(CultureInfo.InvariantCulture);
}
