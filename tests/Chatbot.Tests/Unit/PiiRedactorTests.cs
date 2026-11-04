using System.Diagnostics;
using Chatbot.Application.Chat;
using Chatbot.Infrastructure.Privacy;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Unit;

/// <summary>
/// Every number below is MADE UP: checksums are computed here, none belongs to a person. The tests also measure the false
/// positive rate on ordinary sentences about time, dose-free everyday numbers and counts.
/// </summary>
public class PiiRedactorTests
{
    private readonly RulePiiRedactor _redactor = new();

    /// <summary>Builds a valid (made up) national id number from nine digits.</summary>
    private static string MadeUpTckn(string nineDigits)
    {
        var d = nineDigits.Select(c => c - '0').ToArray();
        var tenth = (((d[0] + d[2] + d[4] + d[6] + d[8]) * 7) - (d[1] + d[3] + d[5] + d[7])) % 10;
        tenth = (tenth + 10) % 10;
        var eleventh = (d.Sum() + tenth) % 10;
        return nineDigits + tenth + eleventh;
    }

    private const string SampleIban = "TR330006100519786457841326"; // the documented example IBAN, not an account of a person

    private static string Spaced(string iban) => string.Join(' ', Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - (i * 4)))));

    [Fact]
    public void TheMadeUpNumbers_ArePlausible_ForTheTestsBelow()
    {
        Assert.True(RulePiiRedactor.IsValidTckn(MadeUpTckn("123456789")));
        Assert.True(RulePiiRedactor.IsValidTckn("10000000146"));
        Assert.False(RulePiiRedactor.IsValidTckn("10000000147"));
        Assert.True(RulePiiRedactor.IsValidIban(SampleIban));
        Assert.False(RulePiiRedactor.IsValidIban("TR330006100519786457841327"));
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("987654321")]
    [InlineData("246813579")]
    public void ANationalIdNumber_WithAValidChecksum_IsMasked(string nine)
    {
        var id = MadeUpTckn(nine);
        Assert.Equal("TC kimlik no: [TC] olarak kayıtlı.", _redactor.Redact($"TC kimlik no: {id} olarak kayıtlı."));
    }

    [Fact]
    public void AnElevenDigitNumber_WithABadChecksum_IsLeftAlone_ItIsNotAnId() =>
        Assert.Equal("Dosya numaram 12345678901.", _redactor.Redact("Dosya numaram 12345678901."));

    [Theory]
    [InlineData("0532 123 45 67")]
    [InlineData("05321234567")]
    [InlineData("+90 532 123 45 67")]
    [InlineData("+905321234567")]
    [InlineData("0090 532 123 45 67")]
    [InlineData("532-123-45-67")]
    [InlineData("0 (532) 123 45 67")]
    [InlineData("(0212) 555 12 34")]
    [InlineData("0212 555 12 34")]
    [InlineData("0 (212) 555 12 34")]
    [InlineData("+90 212 555 12 34")]
    [InlineData("(312) 555 12 34")]
    public void APhoneNumber_InTheUsualTurkishFormats_IsMasked(string phone) =>
        Assert.Equal("Beni [TELEFON] numarasından ara.", _redactor.Redact($"Beni {phone} numarasından ara."));

    [Theory]
    [InlineData("ayse.yilmaz@example.com")]
    [InlineData("a_b+test@mail.alt.example.org")]
    public void AnEmailAddress_IsMasked(string email) =>
        Assert.Equal("Yaz: [E-POSTA] lütfen.", _redactor.Redact($"Yaz: {email} lütfen."));

    [Theory]
    [InlineData("https://example.com/yol?kisi=1")]
    [InlineData("http://example.org")]
    [InlineData("www.example.com/sayfa")]
    public void AUrl_IsMasked(string url) =>
        Assert.Equal("Baktığım sayfa [BAGLANTI] idi.", _redactor.Redact($"Baktığım sayfa {url} idi."));

    [Fact]
    public void AnIban_WithAValidChecksum_IsMasked_WithOrWithoutSpaces()
    {
        Assert.Equal("Hesap: [IBAN].", _redactor.Redact($"Hesap: {SampleIban}."));
        Assert.Equal("Hesap: [IBAN].", _redactor.Redact($"Hesap: {Spaced(SampleIban)}."));
        Assert.Equal("Hesap: [IBAN] değil.", _redactor.Redact("Hesap: tr330006100519786457841326 değil.")); // lower case too
    }

    [Fact]
    public void AnIban_WithABadChecksum_IsLeftAlone() =>
        Assert.Equal("Kod TR330006100519786457841327 idi.", _redactor.Redact("Kod TR330006100519786457841327 idi."));

    [Theory]
    [InlineData("4111 1111 1111 1111")] // a well-known TEST card number
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111111111111111")]
    [InlineData("5555 5555 5555 4444")]
    [InlineData("3782 822463 10005")]
    public void ALongCardLikeDigitSequence_IsMasked(string card) =>
        Assert.Equal("Kartım [KART] idi.", _redactor.Redact($"Kartım {card} idi."));

    [Theory]
    [InlineData("12.03.1985")]
    [InlineData("1/2/1985")]
    [InlineData("05-03-1985")]
    [InlineData("1985-03-12")]
    [InlineData("12 Mart 1985")]
    [InlineData("3 ağustos 2001")]
    public void ADateOfBirthLikePattern_IsMasked(string date) =>
        Assert.Equal("Doğum tarihim [TARIH] dir.", _redactor.Redact($"Doğum tarihim {date} dir."));

    [Fact]
    public void ManyKindsInOneText_AreAllMasked_AndTheRestIsKept()
    {
        var id = MadeUpTckn("135792468");
        var text = $"Adım yok ama {id} numaralı, 0532 123 45 67 telefonlu, a@b.example.com adresli ve 12.03.1985 doğumluyum; hastane yolu 3 km.";
        var masked = _redactor.Redact(text);
        Assert.Equal("Adım yok ama [TC] numaralı, [TELEFON] telefonlu, [E-POSTA] adresli ve [TARIH] doğumluyum; hastane yolu 3 km.", masked);
    }

    [Fact]
    public void MaskingTwice_ChangesNothingMore()
    {
        var once = _redactor.Redact($"{MadeUpTckn("135792468")} ve 0532 123 45 67 ve {SampleIban} ve 12.03.1985");
        Assert.Equal(once, _redactor.Redact(once));
    }

    [Fact]
    public void NothingIsLeftOfTheIdentifiers_InTheMaskedText()
    {
        var id = MadeUpTckn("135792468");
        var masked = _redactor.Redact($"{id} 0532 123 45 67 a@b.example.com {SampleIban} 4111 1111 1111 1111 12.03.1985 https://example.com/x");
        foreach (var leaked in new[] { id, "123 45 67", "a@b", "6457841326", "1111 1111", "1985", "example.com" })
        {
            Assert.False(masked.Contains(leaked, StringComparison.Ordinal), $"'{leaked}' is still in: {masked}");
        }
    }

    /// <summary>
    /// The false positive rate on ordinary sentences: counts, times, ages, durations, prices, ratios. None may change.
    /// </summary>
    [Fact]
    public void OrdinarySentencesWithNumbers_AreNotChanged_FalsePositiveRateIsZeroOnTheCorpus()
    {
        string[] corpus =
        [
            "3 hafta önce başladı ve her gün yoruluyorum.",
            "Yüzde 40 daha iyi hissediyorum.",
            "Saat 14.30'da uyandım, 15:45'te tekrar uyudum.",
            "Bu hafta 2 kez doktoru aradım.",
            "25 yaşındayım ve 2 çocuğum var.",
            "1 ay oldu, 10 gün daha var.",
            "Gece 3'te uyanıp 4'e kadar uyuyamadım.",
            "2024 yılında başladı, 2025'te devam etti.",
            "5 kişilik bir aileyiz, 3 kardeşim var.",
            "Aylık 1000 tl ödüyorum, 2.500 tl daha lazım.",
            "Kilom 62.5 oldu, boyum 1.68.",
            "Tansiyonum 12/8 çıktı, nabzım 72.",
            "Her sabah 7:45'te 20 dakika yürüyorum.",
            "3.5 saat sürdü, 0.5 litre su içtim.",
            "Mart ayında 12 gün, Nisan'da 8 gün izin aldım.",
            "Oda numarası 305, kat 3, blok 12.",
            "İlk 10 dakika çok zordu, sonraki 50 dakika iyiydi.",
            "Evde 4 kişi, sokakta 120 metre mesafede yaşıyoruz.",
            "Haftada 3 gün, günde 2 kez, 15 dakika.",
            "1980'lerde doğan annem 2010'da emekli oldu.",
            "Pazartesi 9'da, salı 14'te randevum var.",
            "Yaklaşık 6 ay önce, 18 yaşından beri.",
            "100 üzerinden 85 puan verdim.",
            "Sayfa 123, madde 45, paragraf 6.",
            "Sıra numaram 4567 idi.",
            "Toplam 2500 adım attım, 35 dakika sürdü.",
            "Saat 23.45'te yatıyorum, 6.30'da kalkıyorum.",
            "3 aydır, 12 haftadır, 90 gündür böyleyim.",
            "5 yıl önce, 10 yıl sonra, 1 yıl içinde.",
            "Bugün 4. gün, 2. hafta, 3. ay.",
            "Doz değil, sadece sayı: 1 2 3 4 5.",
            "Mahalle muhtarlığı 3 numaralı sokakta.",
            "Telefonum çalmıyor ama numaramı vermeyeceğim.",
            "Kod 1234 ile girdim.",
            "Plakası 34 ABC 123 olan araç.",
            "1.000.000 kere düşündüm.",
            "Ortalama 8-10 saat uyuyorum, 2-3 kez uyanıyorum.",
            "12 ila 15 kişi vardı.",
            "Ayın 15'inde, yılın 3. ayında.",
            "Haziran ayının ilk haftası, 2023 yazı.",
        ];

        var changed = corpus.Where(sentence => _redactor.Redact(sentence) != sentence).ToList();
        Assert.True(changed.Count == 0, $"False positives ({changed.Count} of {corpus.Length}): " + string.Join(" | ", changed));
    }

    [Fact]
    public void KnownAndAcceptedFalsePositive_AFullDate_IsMaskedEvenWhenItIsNotABirthDate() =>
        Assert.Equal("Kontrol [TARIH] günü.", _redactor.Redact("Kontrol 14.11.2025 günü."));

    [Fact]
    public void NamesAndIndirectIdentifiers_CannotBeCaught_ThisIsADocumentedLimit()
    {
        const string text = "Ben Ayşe Demir, Ankara'daki onkoloji servisinde hemşire olan kardeşim Fatma ile konuştum.";
        Assert.Equal(text, _redactor.Redact(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyText_IsReturnedAsIs(string text) => Assert.Equal(text, _redactor.Redact(text));

    [Fact]
    public void AHugeAdversarialText_IsHandledQuickly_AndNeverHangs()
    {
        var text = string.Concat(Enumerable.Repeat("1 ", 40_000)) + string.Concat(Enumerable.Repeat("a@", 20_000)) + string.Concat(Enumerable.Repeat("0 (", 20_000));
        var watch = Stopwatch.StartNew();
        var result = _redactor.Redact(text);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.NotNull(result);
    }

    [Fact]
    public void TheRealRedactor_IsRegistered_NotTheNoopOne()
    {
        using var provider = TestServices.Build("Production");
        Assert.IsType<RulePiiRedactor>(provider.GetRequiredService<IPiiRedactor>());
    }

    [Fact]
    public void ItNeverChangesWhatIsStored_ItOnlyReturnsAMaskedCopy()
    {
        var original = $"numaram 0532 123 45 67";
        var copy = _redactor.Redact(original);
        Assert.Equal("numaram 0532 123 45 67", original);
        Assert.NotEqual(original, copy);
    }
}
