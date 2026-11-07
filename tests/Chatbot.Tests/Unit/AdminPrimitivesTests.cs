using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Chatbot.Application.Admin;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Infrastructure.Admin;

namespace Chatbot.Tests.Unit;

public class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Rfc4648_Vectors(string text, string expected)
    {
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(text)));
        if (text.Length > 0)
        {
            Assert.Equal(text, Encoding.ASCII.GetString(Base32.Decode(expected)!));
        }
    }

    [Fact]
    public void Decoding_IgnoresCaseSpacesHyphensAndPadding_AndRefusesOtherCharacters()
    {
        Assert.Equal("foobar", Encoding.ASCII.GetString(Base32.Decode("mzxw 6ytb-oi======")!));
        Assert.Null(Base32.Decode("MZXW1"));
        Assert.Null(Base32.Decode(null));
        Assert.Null(Base32.Decode(string.Empty));
    }

    [Fact]
    public void ARandomSecret_RoundTrips()
    {
        var secret = Totp.NewSecret();
        Assert.Equal(Totp.SecretBytes, secret.Length);
        Assert.Equal(secret, Base32.Decode(Base32.Encode(secret)));
    }
}

public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238, Appendix B, SHA-1 (8 digits). The application uses 6 digits: the last six of these.
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Rfc6238_TestVectors(long unixTime, string expected8)
    {
        var step = unixTime / Totp.StepSeconds;
        Assert.Equal(expected8, Totp.Compute(RfcSecret, step, digits: 8));
        Assert.Equal(expected8[2..], Totp.Compute(RfcSecret, step));
    }

    private static DateTimeOffset At(long step) => DateTimeOffset.FromUnixTimeSeconds(step * Totp.StepSeconds + 5);

    [Fact]
    public void ACodeOfTheCurrentStep_IsAccepted_AndReturnsItsStep()
    {
        var step = 50_000_000L;
        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step), At(step), 0));
    }

    [Fact]
    public void TheWindow_IsOneStepEitherWay_NotMore()
    {
        var step = 50_000_000L;
        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), At(step), 0));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), At(step), 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 2), At(step), 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 2), At(step), 0));
    }

    [Fact]
    public void ACodeIsAcceptedOnce_TheSameOrAnOlderStepIsAReplay()
    {
        var step = 50_000_000L;
        var code = Totp.Compute(RfcSecret, step);
        var accepted = Totp.Verify(RfcSecret, code, At(step), 0);
        Assert.Equal(step, accepted);
        Assert.Null(Totp.Verify(RfcSecret, code, At(step), accepted!.Value)); // the same code again
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), At(step), accepted.Value)); // an older one
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), At(step), accepted.Value)); // the next one is fine
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    [InlineData("١٢٣٤٥٦")]
    public void MalformedCodes_AreRefused(string? code) =>
        Assert.Null(Totp.Verify(RfcSecret, code, At(50_000_000L), 0));

    [Fact]
    public void TheUri_CarriesTheSecretIssuerAndTheStandardParameters()
    {
        var uri = Totp.Uri("Lumind", "arastirmaci-1", "MZXW6YTB");
        Assert.StartsWith("otpauth://totp/Lumind:arastirmaci-1?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=MZXW6YTB", uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1&digits=6&period=30", uri, StringComparison.Ordinal);
    }
}

public class AdminPasswordTests
{
    private static readonly AdminPasswordHasher Fast = new(1000); // a low cost keeps the unit tests quick; the default is tested below

    [Fact]
    public void AHash_Verifies_OnlyTheRightPassword_AndIsSalted()
    {
        var hash = Fast.Hash("correct horse battery");
        Assert.StartsWith("pbkdf2-sha512$1000$", hash, StringComparison.Ordinal);
        Assert.True(Fast.Verify("correct horse battery", hash));
        Assert.False(Fast.Verify("correct horse batterY", hash));
        Assert.NotEqual(hash, Fast.Hash("correct horse battery"));
        Assert.DoesNotContain("correct", hash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("pbkdf2-sha512$x$y$z")]
    [InlineData("pbkdf2-sha512$1000$!!!$!!!")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void ABrokenOrMissingHash_IsNeverAMatch_AndNeverThrows(string? stored)
    {
        Assert.False(Fast.Verify("whatever", stored));
        Assert.True(Fast.NeedsRehash(stored));
    }

    [Fact]
    public void AnOlderCost_NeedsARehash_ACurrentOneDoesNot()
    {
        var old = new AdminPasswordHasher(500).Hash("some long password here");
        Assert.True(Fast.NeedsRehash(old));
        Assert.True(Fast.Verify("some long password here", old)); // the old hash still verifies with ITS iteration count
        Assert.False(Fast.NeedsRehash(Fast.Hash("x")));
    }

    [Fact]
    public void TheDefaultCost_IsAtLeastTheOwaspMinimum()
    {
        var options = new AdminAuthOptions();
        Assert.True(options.Pbkdf2Iterations >= 210_000);
        Assert.StartsWith($"pbkdf2-sha512${options.Pbkdf2Iterations}$", new AdminPasswordHasher(options.Pbkdf2Iterations).Hash("a long enough password"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short", "bob", true)]
    [InlineData("exactly-14-char", "bob", false)]
    [InlineData("0123456789abc", "bob", true)]
    [InlineData("arastirmaci-1-extra", "arastirmaci-1", false)]
    public void ThePasswordRule_IsLengthAndNotTheUserName(string password, string user, bool problem) =>
        Assert.Equal(problem, AdminPasswordHasher.PasswordProblem(password, user, 14) is not null);

    [Fact]
    public void ThePassword_MayNotEqualTheUserName_EvenInAnotherCase() =>
        Assert.NotNull(AdminPasswordHasher.PasswordProblem("Arastirmaci-Bir-Ad", "arastirmaci-bir-ad", 14));
}

public class AdminRecoveryAndLockTests
{
    [Fact]
    public void RecoveryCodes_AreTenCharactersFromTheCodeAlphabet_ShownWithAHyphen()
    {
        var code = RecoveryCodes.New();
        Assert.Equal(RecoveryCodes.Length, code.Length);
        Assert.All(code, c => Assert.Contains(c, Domain.Rules.ParticipantCode.Alphabet));
        Assert.Equal(code, RecoveryCodes.Normalize(RecoveryCodes.Display(code).ToLowerInvariant()));
        Assert.Null(RecoveryCodes.Normalize("SHORT"));
        Assert.Null(RecoveryCodes.Normalize("ABCDEFGHIO")); // O is not in the alphabet
        Assert.Equal(10, Enumerable.Range(0, 10).Select(_ => RecoveryCodes.New()).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(7, 4)]
    [InlineData(8, 8)]
    [InlineData(9, 15)]
    [InlineData(10, 15)]
    [InlineData(500, 15)]
    public void TheLock_GrowsFromTheFifthFailure_AndNeverPastTheMaximum(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), AdminLockSchedule.LockFor(failures, 5, 15));

    [Fact]
    public void TheTracker_LocksAnUnknownNameOnTheSameSchedule_AsARealAccount()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracker = new AdminLoginTracker(clock);
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(TimeSpan.Zero, tracker.RecordFailure("kimse", 5, 15));
        }

        Assert.Equal(TimeSpan.FromMinutes(1), tracker.RecordFailure("kimse", 5, 15));
        Assert.True(tracker.RemainingLock("kimse") > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, tracker.RemainingLock("baska"));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.Zero, tracker.RemainingLock("kimse"));
    }
}

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public class AdminStatsMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero); // a Monday

    [Fact]
    public void ActiveTime_IsTheSumOfTheGapsBelowTheThreshold()
    {
        var gap = TimeSpan.FromMinutes(10);
        var times = new[] { T0, T0.AddMinutes(2), T0.AddMinutes(5), T0.AddMinutes(45), T0.AddMinutes(48) };
        Assert.Equal(TimeSpan.FromMinutes(2 + 3 + 3), AdminStatsMath.EstimatedActiveTime(times, gap)); // the 40 minute gap is not counted
    }

    [Fact]
    public void ActiveTime_Edges()
    {
        var gap = TimeSpan.FromMinutes(10);
        Assert.Equal(TimeSpan.Zero, AdminStatsMath.EstimatedActiveTime([], gap));
        Assert.Equal(TimeSpan.Zero, AdminStatsMath.EstimatedActiveTime([T0], gap));
        Assert.Equal(TimeSpan.Zero, AdminStatsMath.EstimatedActiveTime([T0, T0.AddMinutes(10)], gap)); // exactly the threshold: not below it
        Assert.Equal(TimeSpan.FromMinutes(9), AdminStatsMath.EstimatedActiveTime([T0.AddMinutes(9), T0], gap)); // order does not matter
        Assert.Equal(TimeSpan.Zero, AdminStatsMath.EstimatedActiveTime([T0, T0], gap));
    }

    [Fact]
    public void TheState_IsCompletedOngoingOrAbandoned()
    {
        var abandonedAfter = TimeSpan.FromHours(24);
        Assert.Equal(ConversationState.Completed, AdminStatsMath.StateOf(T0.AddHours(1), T0, T0.AddDays(30), abandonedAfter));
        Assert.Equal(ConversationState.Ongoing, AdminStatsMath.StateOf(null, T0, T0.AddHours(23), abandonedAfter));
        Assert.Equal(ConversationState.Ongoing, AdminStatsMath.StateOf(null, T0, T0.AddHours(24), abandonedAfter)); // exactly the limit
        Assert.Equal(ConversationState.Abandoned, AdminStatsMath.StateOf(null, T0, T0.AddHours(24).AddSeconds(1), abandonedAfter));
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    [InlineData("\tx", "'\tx")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData(null, "")]
    public void CsvCells_AreEscapedAgainstFormulaInjectionAndQuoted(string? value, string expected) =>
        Assert.Equal(expected, AdminStatsMath.CsvCell(value));

    [Fact]
    public void TheCsvHeader_HasNoTextColumn_AndEachRowHasTheSameNumberOfCells()
    {
        var header = AdminStatsMath.CsvHeader.Split(',');
        Assert.DoesNotContain(header, h => h is "content" or "text" or "nickname" or "message" or "category" or "code");
        var stats = new ParticipantStats(Guid.NewGuid(), "=K-001", ParticipantStatus.Active, T0, T0, T0, 3, 2, 1, 1, 0, 5, 5, T0, 12.5);
        var line = AdminStatsMath.CsvLine(stats);
        Assert.StartsWith("'=K-001,Active,", line, StringComparison.Ordinal);
        Assert.Equal(header.Length, line.Split(',').Length);
        Assert.EndsWith(",12.5", line, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", AdminStatsMath.Csv([stats]), StringComparison.Ordinal);
    }

    [Fact]
    public void PeriodStarts_AreUtcDaysAndMondays()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero), AdminStatsMath.PeriodStart(new DateTimeOffset(2026, 3, 4, 23, 59, 0, TimeSpan.Zero), weekly: false));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), AdminStatsMath.PeriodStart(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero), weekly: true)); // Sunday -> that week's Monday
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), AdminStatsMath.PeriodStart(T0, weekly: true));
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), AdminStatsMath.PeriodStart(new DateTimeOffset(2026, 3, 3, 1, 0, 0, TimeSpan.FromHours(3)), weekly: true)); // 22:00 UTC on Monday
    }
}

public class AdminOptionsTests
{
    private static AuthOptions Auth => new() { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40) };

    private static AdminAuthOptions Good => new() { JwtSigningKey = new string('a', 40) };

    [Fact]
    public void TheDefaults_AreValid() => Assert.Empty(OptionsValidator.ValidateAdmin(Good, Auth));

    [Fact]
    public void ANeedsASeparateKey()
    {
        Assert.Contains(OptionsValidator.ValidateAdmin(new AdminAuthOptions(), Auth), p => p.Contains("Admin:JwtSigningKey", StringComparison.Ordinal));
        var same = new AdminAuthOptions { JwtSigningKey = Auth.JwtSigningKey };
        Assert.Contains(OptionsValidator.ValidateAdmin(same, Auth), p => p.Contains("must differ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMessagesNameTheSettingNeverTheValue()
    {
        var bad = new AdminAuthOptions { JwtSigningKey = "short-secret-value" };
        Assert.All(OptionsValidator.ValidateAdmin(bad, Auth), p => Assert.DoesNotContain("short-secret-value", p, StringComparison.Ordinal));
    }

    [Fact]
    public void WeakSettings_AreRefused()
    {
        var weak = Good;
        weak.Pbkdf2Iterations = 1000;
        weak.PasswordMinLength = 8;
        weak.IdleMinutes = 60 * 9;
        weak.MaxBulkCreate = 10_000;
        weak.Audience = Auth.Audience;
        var problems = OptionsValidator.ValidateAdmin(weak, Auth);
        Assert.Contains(problems, p => p.Contains("Pbkdf2Iterations", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("PasswordMinLength", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("IdleMinutes", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("MaxBulkCreate", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("must all be different", StringComparison.Ordinal));
    }
}

/// <summary>
/// "There is no content in the panel." Two nets: the wire types of the admin API have no text-like property, and the admin
/// code never reads the content or the nickname column.
/// </summary>
public class AdminContentFreeTests
{
    private static readonly string[] ForbiddenNames = ["Content", "Text", "Nickname", "Phrase", "Category", "Body", "Message", "Reply"];

    [Fact]
    public void NoAdminWireType_HasATextLikeProperty()
    {
        var types = typeof(StatsSummary).Assembly.GetTypes()
            .Where(t => t.Namespace == "Chatbot.Application.Admin" && t.IsClass)
            .Concat(typeof(Chatbot.Api.Controllers.AdminController).Assembly.GetTypes()
                .Where(t => t.Namespace == "Chatbot.Api.Controllers" && t.Name.StartsWith("Admin", StringComparison.Ordinal) && t.IsClass))
            .ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain(property.Name, ForbiddenNames);
            }
        }
    }

    [Fact]
    public void TheAdminCode_NeverNamesTheContentOrNicknameColumns()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        var src = Path.Combine(dir!.FullName, "src");
        var files = new[]
        {
            Path.Combine(src, "Chatbot.Infrastructure", "Admin"),
            Path.Combine(src, "Chatbot.Application", "Admin"),
        }.SelectMany(d => Directory.EnumerateFiles(d, "*.cs"))
            .Concat([Path.Combine(src, "Chatbot.Api", "Controllers", "AdminControllers.cs")])
            .ToList();
        Assert.True(files.Count >= 6);

        foreach (var file in files.Where(f => !f.EndsWith("DevTestAdminService.cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, @"\.(Content|Nickname)\b"), $"{Path.GetFileName(file)} reads the content or the nickname.");
            Assert.False(Regex.IsMatch(text, @"\bMessage\.Content\b|\bParticipant\.Nickname\b"), Path.GetFileName(file));
        }
    }
}
