using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;

namespace Chatbot.Tests.Unit;

public class ParticipantCodeTests
{
    [Theory]
    [InlineData("abcd2345", "ABCD2345")]
    [InlineData("abcd-2345", "ABCD2345")]
    [InlineData("  ab cd - 23 45 ", "ABCD2345")]
    [InlineData("ABCD\t2345\n", "ABCD2345")]
    public void Normalize_UppercasesAndDropsSpacesAndHyphens(string raw, string expected) =>
        Assert.Equal(expected, ParticipantCode.Normalize(raw));

    [Fact]
    public void Normalize_KeepsCharactersOutsideTheAlphabet_SoTypingMistakesStayVisible() =>
        Assert.Equal("ABCD234O", ParticipantCode.Normalize("abcd234o"));

    [Fact]
    public void Normalize_IsCultureIndependent_TurkishDottedLettersStayInvalid()
    {
        // Turkish "i" must become "I" (not in the alphabet), never "İ".
        Assert.Equal("ABCDI234", ParticipantCode.Normalize("abcdi234"));
        Assert.False(ParticipantCode.IsValid(ParticipantCode.Normalize("abcdi234")));
    }

    [Theory]
    [InlineData("ABCD2345", true)]
    [InlineData("BCDF3456", true)]
    [InlineData("ABCD234", false)] // too short
    [InlineData("ABCD23456", false)] // too long
    [InlineData("ABCD2340", false)] // 0 is not in the alphabet
    [InlineData("ABCD2341", false)] // 1 is not in the alphabet
    [InlineData("ABCD234O", false)] // O is not in the alphabet
    [InlineData("ABCD234L", false)] // L is not in the alphabet
    [InlineData("", false)]
    public void IsValid_FollowsTheAlphabetAndLength(string normalized, bool expected) =>
        Assert.Equal(expected, ParticipantCode.IsValid(normalized));

    [Fact]
    public void Normalize_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, ParticipantCode.Normalize(null));
        Assert.Equal(string.Empty, ParticipantCode.Normalize(""));
    }
}

public class NicknameRulesTests
{
    [Theory]
    [InlineData("Deneme")]
    [InlineData("Ay")]
    [InlineData("Şükrü Çağlar")]
    [InlineData("mavi_kus-2")]
    [InlineData("İpek")]
    [InlineData("  Mavi  ")] // trimmed first
    [InlineData("12345")] // up to 6 digits in a row are fine
    public void Validate_AcceptsTurkishLettersDigitsSpacesHyphenAndUnderscore(string nickname) =>
        Assert.Null(NicknameRules.Validate(nickname));

    [Theory]
    [InlineData("", NicknameProblem.TooShort)]
    [InlineData("a", NicknameProblem.TooShort)]
    [InlineData("   ", NicknameProblem.TooShort)]
    [InlineData(null, NicknameProblem.TooShort)]
    [InlineData("abcdefghijklmnopqrstu", NicknameProblem.TooLong)] // 21
    [InlineData("ali@posta", NicknameProblem.Personal)]
    [InlineData("tel 5551234567", NicknameProblem.Personal)]
    [InlineData("1234567", NicknameProblem.Personal)] // 7 digits in a row
    [InlineData("mavi!", NicknameProblem.InvalidChars)]
    [InlineData("mavi.kus", NicknameProblem.InvalidChars)]
    [InlineData("<b>x</b>", NicknameProblem.InvalidChars)]
    public void Validate_RejectsWithTheSameReasonsAsTheClient(string? nickname, NicknameProblem expected) =>
        Assert.Equal(expected, NicknameRules.Validate(nickname));

    [Fact]
    public void Validate_CountsCharactersNotUtf16Units()
    {
        // 20 emoji-free characters including Turkish ones: exactly the limit.
        Assert.Null(NicknameRules.Validate(new string('ş', NicknameRules.MaxLength)));
        Assert.Equal(NicknameProblem.TooLong, NicknameRules.Validate(new string('ş', NicknameRules.MaxLength + 1)));
    }

    [Fact]
    public void Clean_Trims() => Assert.Equal("Mavi", NicknameRules.Clean("  Mavi "));
}

public class ConsentRulesTests
{
    private static readonly DocumentVersions Current = new("0.1-taslak", "0.1-taslak");
    private static readonly Guid Pid = Guid.CreateVersion7();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static ConsentRecord Rec(ConsentType type, string version, bool granted, int minutes) =>
        new() { Id = Guid.CreateVersion7(), ParticipantId = Pid, Type = type, DocumentVersion = version, Granted = granted, Timestamp = T0.AddMinutes(minutes) };

    private static Participant Person(string? nickname = null) =>
        new() { Id = Pid, CodeHash = "x", Status = ParticipantStatus.Active, Nickname = nickname };

    private static ParticipantState State(string? nickname, params ConsentRecord[] records) =>
        ConsentRules.ComputeState(Person(nickname), ConsentRules.Snapshot(records));

    private static bool Onboarded(params ConsentRecord[] records) =>
        ConsentRules.IsOnboarded(ConsentRules.Snapshot(records), Current);

    [Fact]
    public void NewParticipant_HasNothing()
    {
        var state = State(null);
        Assert.Equal(Pid, state.ParticipantId);
        Assert.Null(state.Nickname);
        Assert.Null(state.NoticeAcknowledgedVersion);
        Assert.Null(state.Consent);
        Assert.False(Onboarded());
    }

    [Fact]
    public void EverythingDone_IsOnboardedAndHasTheFullState()
    {
        var records = new[]
        {
            Rec(ConsentType.PrivacyNotice, "0.1-taslak", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 2),
        };
        var state = State("Deneme", records);
        Assert.Equal("Deneme", state.Nickname);
        Assert.Equal("0.1-taslak", state.NoticeAcknowledgedVersion);
        Assert.Equal(new ConsentState("0.1-taslak", true, false), state.Consent);
        Assert.True(Onboarded(records));
    }

    [Fact]
    public void ResearchUse_IsReportedSeparately()
    {
        var state = State(null,
            Rec(ConsentType.PrivacyNotice, "0.1-taslak", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 2),
            Rec(ConsentType.ResearchUse, "0.1-taslak", true, 2));
        Assert.True(state.Consent!.ResearchUse);
    }

    [Fact]
    public void WithdrawnConsent_IsNull_AndNotOnboarded_ButTheNoticeStays()
    {
        var records = new[]
        {
            Rec(ConsentType.PrivacyNotice, "0.1-taslak", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 2),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", false, 30),
        };
        var state = State("Deneme", records);
        Assert.Null(state.Consent);
        Assert.Equal("0.1-taslak", state.NoticeAcknowledgedVersion);
        Assert.False(Onboarded(records));
    }

    [Fact]
    public void ConsentGivenAgainAfterWithdrawal_IsValidAgain()
    {
        var records = new[]
        {
            Rec(ConsentType.PrivacyNotice, "0.1-taslak", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 2),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", false, 30),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 60),
        };
        Assert.True(Onboarded(records));
        Assert.NotNull(State(null, records).Consent);
    }

    [Fact]
    public void OldVersions_AreReportedWithTheirVersion_ButAreNotValid()
    {
        var records = new[]
        {
            Rec(ConsentType.PrivacyNotice, "0.0-eski", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.0-eski", true, 2),
        };
        var state = State("Deneme", records);
        Assert.Equal("0.0-eski", state.NoticeAcknowledgedVersion);
        Assert.Equal("0.0-eski", state.Consent!.Version);
        Assert.False(ConsentRules.HasValidConsent(ConsentRules.Snapshot(records), Current));
        Assert.False(ConsentRules.IsNoticeCurrent(ConsentRules.Snapshot(records), Current));
        Assert.False(Onboarded(records));
    }

    [Fact]
    public void CurrentConsent_WithOutdatedNotice_IsNotOnboarded()
    {
        var records = new[]
        {
            Rec(ConsentType.PrivacyNotice, "0.0-eski", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 2),
        };
        Assert.True(ConsentRules.HasValidConsent(ConsentRules.Snapshot(records), Current));
        Assert.False(Onboarded(records));
    }

    [Fact]
    public void NoticeAlone_IsNotEnough() =>
        Assert.False(Onboarded(Rec(ConsentType.PrivacyNotice, "0.1-taslak", true, 1)));

    [Fact]
    public void ConsentAlone_WithoutTheNotice_IsNotEnough() =>
        Assert.False(Onboarded(Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 1)));

    [Fact]
    public void TheLatestRecordOfATypeWins_NotTheOrderOfTheList()
    {
        var older = Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 1);
        var newer = Rec(ConsentType.ExplicitConsent, "0.1-taslak", false, 5);
        var snapshot = ConsentRules.Snapshot([newer, older]);
        Assert.False(snapshot.LatestExplicitConsent!.Value.Granted);
    }

    [Fact]
    public void ResearchUseWithdrawn_KeepsTheProcessingConsent()
    {
        var state = State(null,
            Rec(ConsentType.PrivacyNotice, "0.1-taslak", true, 1),
            Rec(ConsentType.ExplicitConsent, "0.1-taslak", true, 2),
            Rec(ConsentType.ResearchUse, "0.1-taslak", true, 2),
            Rec(ConsentType.ResearchUse, "0.1-taslak", false, 9));
        Assert.False(state.Consent!.ResearchUse);
        Assert.True(state.Consent.Processing);
    }
}
