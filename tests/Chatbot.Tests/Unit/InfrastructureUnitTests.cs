using System.Text;
using Chatbot.Api.Startup;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Auth;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Dev;
using Chatbot.Infrastructure.Security;
using Chatbot.Infrastructure.Seed;
using Chatbot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Chatbot.Tests.Unit;

internal static class TestServices
{
    public static Dictionary<string, string?> Secrets(string pepper = "p-pepper-p-pepper-p-pepper-p-pepper-") => new()
    {
        ["Auth:CodePepper"] = pepper,
        ["Auth:JwtSigningKey"] = new string('j', 40),
        ["Privacy:IpHashKey"] = new string('i', 40),
        [$"Privacy:DataEncryptionKeys:{TestEncryption.KeyId}"] = TestEncryption.KeyBase64,
        ["Privacy:CurrentDataEncryptionKeyId"] = TestEncryption.KeyId,
        ["ConnectionStrings:Default"] = "Host=none",
    };

    public static ServiceProvider Build(string environment, Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? Secrets()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environment));
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public class SecretHasherTests
{
    private static SecretHasher Hasher(string pepper = "pepper-one-pepper-one-pepper-one-1234") =>
        new(new AuthOptions { CodePepper = pepper }, new PrivacyOptions { IpHashKey = new string('k', 40) });

    [Fact]
    public void TheCodeHash_IsStableHmac_NotThePlainCode()
    {
        var hasher = Hasher();
        var hash = hasher.HashCode("ABCD2345");
        Assert.Equal(hash, hasher.HashCode("ABCD2345"));
        Assert.Equal(64, hash.Length); // hex of 32 bytes
        Assert.DoesNotContain("ABCD2345", hash, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(hash, hasher.HashCode("ABCD2346"));
    }

    [Fact]
    public void TheCodeHash_DependsOnThePepper()
    {
        Assert.NotEqual(Hasher("pepper-one-pepper-one-pepper-one-1234").HashCode("ABCD2345"),
            Hasher("pepper-two-pepper-two-pepper-two-5678").HashCode("ABCD2345"));
    }

    [Fact]
    public void TheCodeHash_MatchesAnIndependentHmacSha256()
    {
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("pepper-one-pepper-one-pepper-one-1234"), Encoding.UTF8.GetBytes("ABCD2345")));
        Assert.Equal(expected, Hasher().HashCode("ABCD2345"));
    }

    [Fact]
    public void TheIpHash_UsesItsOwnKey_NotTheCodePepper()
    {
        var one = new SecretHasher(new AuthOptions { CodePepper = new string('p', 40) }, new PrivacyOptions { IpHashKey = new string('a', 40) });
        var two = new SecretHasher(new AuthOptions { CodePepper = new string('p', 40) }, new PrivacyOptions { IpHashKey = new string('b', 40) });
        Assert.NotEqual(one.HashIp("10.0.0.1"), two.HashIp("10.0.0.1"));
        Assert.DoesNotContain("10.0.0.1", one.HashIp("10.0.0.1")!, StringComparison.Ordinal);
        Assert.Null(one.HashIp(null));
        Assert.Null(one.HashIp(" "));
    }

    [Fact]
    public void TheRefreshTokenHash_IsSha256Hex()
    {
        var hash = Hasher().HashRefreshToken("some-token");
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("some-token", hash, StringComparison.Ordinal);
    }
}

public class RefreshTokenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static RefreshToken Token(DateTimeOffset? expires = null, DateTimeOffset? family = null, DateTimeOffset? revoked = null, Guid? replacedBy = null) => new()
    {
        Id = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        TokenHash = "h",
        FamilyId = Guid.NewGuid(),
        CreatedAt = Now.AddDays(-1),
        ExpiresAt = expires ?? Now.AddDays(6),
        FamilyExpiresAt = family ?? Now.AddDays(29),
        RevokedAt = revoked,
        ReplacedByTokenId = replacedBy,
    };

    [Fact]
    public void AFreshToken_IsValid() => Assert.Equal(RefreshTokenState.Valid, RefreshTokenRules.Evaluate(Token(), Now));

    [Fact]
    public void AnExpiredToken_IsExpired() =>
        Assert.Equal(RefreshTokenState.Expired, RefreshTokenRules.Evaluate(Token(expires: Now.AddSeconds(-1)), Now));

    [Fact]
    public void TheFamilyCap_EndsASlidingTokenThatWouldStillBeValid() =>
        Assert.Equal(RefreshTokenState.Expired, RefreshTokenRules.Evaluate(Token(family: Now.AddSeconds(-1)), Now));

    [Fact]
    public void ARotatedToken_PresentedAgain_IsReuse() =>
        Assert.Equal(RefreshTokenState.Reused, RefreshTokenRules.Evaluate(Token(replacedBy: Guid.NewGuid()), Now));

    [Fact]
    public void ARevokedToken_PresentedAgain_IsReuse() =>
        Assert.Equal(RefreshTokenState.Reused, RefreshTokenRules.Evaluate(Token(revoked: Now.AddHours(-1)), Now));

    [Fact]
    public void TheNextExpiry_Slides_ButNeverPassesTheFamilyCap()
    {
        Assert.Equal(Now.AddDays(7), RefreshTokenRules.NextExpiry(Now, 7, Now.AddDays(29)));
        Assert.Equal(Now.AddDays(2), RefreshTokenRules.NextExpiry(Now, 7, Now.AddDays(2)));
    }
}

public class OptionsValidatorTests
{
    private static IReadOnlyList<string> Validate(
        AuthOptions? auth = null,
        PrivacyOptions? privacy = null,
        string? connection = "Host=x",
        string[]? cors = null,
        bool requireDatabase = true) =>
        OptionsValidator.Validate(
            auth ?? new AuthOptions { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40) },
            privacy ?? TestEncryption.Privacy(),
            new DocumentsOptions(),
            new ChatOptions(),
            connection,
            cors ?? [],
            requireDatabase);

    [Fact]
    public void CompleteSettings_AreValid() => Assert.Empty(Validate());

    [Fact]
    public void MissingSecrets_AreReportedByName()
    {
        var problems = Validate(new AuthOptions(), new PrivacyOptions(), connection: null);
        var text = string.Join(" ", problems);
        Assert.Contains("Auth:CodePepper", text, StringComparison.Ordinal);
        Assert.Contains("Auth:JwtSigningKey", text, StringComparison.Ordinal);
        Assert.Contains("Privacy:IpHashKey", text, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:Default", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortSecret_IsRejected_AndItsValueNeverAppearsInTheMessage()
    {
        const string shortSecret = "TOP-SECRET-SHORT";
        var problems = string.Join(" ", Validate(new AuthOptions { JwtSigningKey = shortSecret, CodePepper = new string('p', 40) }));
        Assert.Contains("Auth:JwtSigningKey", problems, StringComparison.Ordinal);
        Assert.DoesNotContain(shortSecret, problems, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorsWildcard_IsRejected() =>
        Assert.Contains(Validate(cors: ["https://*.example.com"]), p => p.Contains("wildcard", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void OnlyThePepperIsNeeded_ToGenerateTheSeed()
    {
        var problems = Validate(new AuthOptions { CodePepper = new string('p', 40) }, new PrivacyOptions(), connection: null, requireDatabase: false);
        Assert.Empty(problems);
    }

    [Fact]
    public void NonsenseLifetimes_AreRejected()
    {
        Assert.NotEmpty(Validate(new AuthOptions { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40), AccessTokenMinutes = 0 }));
        Assert.NotEmpty(Validate(new AuthOptions { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40), RefreshTokenDays = 10, RefreshTokenMaxDays = 5 }));
    }
}

public class EnvironmentRulesTests
{
    [Fact]
    public void InDevelopment_TheKeywordCrisisDetector_IsRegistered()
    {
        using var provider = TestServices.Build(Environments.Development);
        Assert.IsType<DevKeywordCrisisDetector>(provider.GetRequiredService<ICrisisDetector>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void OutsideDevelopment_TheCrisisDetectorIsTheNoopOne_NeverTheKeywordStandIn(string environment)
    {
        using var provider = TestServices.Build(environment);
        var detector = provider.GetRequiredService<ICrisisDetector>();
        Assert.IsType<NoopCrisisDetector>(detector);
        Assert.False(detector.IsCrisis("intihar kendime zarar yaşamak istemiyorum ölmek istiyorum"));
    }

    [Fact]
    public void InDevelopment_TheTriggerWords_AreActive_AndFailOnlyTheFirstAttempt()
    {
        using var provider = TestServices.Build(Environments.Development);
        var triggers = provider.GetRequiredService<IDevChatTriggers>();
        var id = Guid.NewGuid();

        var first = Assert.Throws<AppException>(() => triggers.BeforeAccept("/hata", id));
        Assert.Equal(503, first.StatusCode);
        triggers.BeforeAccept("/hata", id); // same client message id: passes on the retry

        var limit = Assert.Throws<AppException>(() => triggers.BeforeAccept("bir şey /limit", Guid.NewGuid()));
        Assert.Equal(429, limit.StatusCode);
        Assert.Equal(20, limit.RetryAfterSeconds);

        Assert.IsType<DevTriggerChatResponder>(provider.GetRequiredService<IChatResponder>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void OutsideDevelopment_TheTriggerWordsDoNothing(string environment)
    {
        using var provider = TestServices.Build(environment);
        var triggers = provider.GetRequiredService<IDevChatTriggers>();
        foreach (var word in new[] { "/hata", "/limit", "/yarim", "/yavas", "/kisa" })
        {
            triggers.BeforeAccept(word, Guid.NewGuid()); // no exception
        }

        Assert.IsType<FakeChatResponder>(provider.GetRequiredService<IChatResponder>());
    }

    [Fact]
    public async Task OutsideDevelopment_TheCutOffTriggerDoesNotCutTheReply()
    {
        using var provider = TestServices.Build("Production");
        var responder = provider.GetRequiredService<IChatResponder>();
        var context = new ConversationContext([new ContextMessage(MessageRole.User, "/yarim bir şey anlatıyorum işte")], Guid.NewGuid());
        var words = new List<string>();
        await foreach (var chunk in responder.StreamReplyAsync(context, default))
        {
            words.Add(chunk);
        }

        Assert.True(words.Count > 6);
    }

    [Fact]
    public async Task InDevelopment_TheCutOffTrigger_CutsTheFirstAttemptAfterSixWords_AndTheRetryCompletes()
    {
        using var provider = TestServices.Build(Environments.Development, TestServices.Secrets().With("Chat:Fake:FirstTokenDelayMs", "0").With("Chat:Fake:TokenDelayMs", "0"));
        var responder = provider.GetRequiredService<IChatResponder>();
        var id = Guid.NewGuid();
        var context = new ConversationContext([new ContextMessage(MessageRole.User, "/yarim bir şey anlatıyorum işte")], id);

        var received = new List<string>();
        var ex = await Assert.ThrowsAsync<AppException>(async () =>
        {
            await foreach (var chunk in responder.StreamReplyAsync(context, default))
            {
                received.Add(chunk);
            }
        });
        Assert.Equal(6, received.Count);
        Assert.Equal(ProblemCodes.Unavailable, ex.Code);

        var retry = new List<string>();
        await foreach (var chunk in responder.StreamReplyAsync(context, default))
        {
            retry.Add(chunk);
        }

        Assert.True(retry.Count > 6);
    }

    [Fact]
    public void GenerateSeed_IsRefusedOutsideDevelopment_AndWritesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "seed-should-not-exist-" + Guid.NewGuid().ToString("N") + ".sql");
        using var provider = TestServices.Build("Production");
        var environment = provider.GetRequiredService<IHostEnvironment>();
        var error = new StringWriter();

        var code = SeedCommand.Run(environment, provider, ["generate-seed", "--out", path], error, new StringWriter());

        Assert.Equal(2, code);
        Assert.Contains("Development", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void GenerateSeed_InDevelopment_WritesUtf8WithoutBom_AndNeverTheCodesOrThePepper()
    {
        const string pepper = "unit-test-pepper-unit-test-pepper-1234";
        var path = Path.Combine(Path.GetTempPath(), "seed-test-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            using var provider = TestServices.Build(Environments.Development, TestServices.Secrets(pepper));
            var output = new StringWriter();
            var error = new StringWriter();
            var code = SeedCommand.Run(provider.GetRequiredService<IHostEnvironment>(), provider, ["generate-seed", "--out", path], error, output);
            Assert.Equal(0, code);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
            var sql = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(pepper, sql, StringComparison.Ordinal);

            // The five NEW plain codes are printed to the terminal, and only there.
            var printed = System.Text.RegularExpressions.Regex.Matches(output.ToString(), @"  (seed-[1-5]): ([A-Z0-9]{8})")
                .Select(m => m.Groups[2].Value).ToList();
            Assert.Equal(5, printed.Count);
            foreach (var plain in printed)
            {
                Assert.DoesNotContain(plain, sql, StringComparison.Ordinal);
            }

            // Nothing else secret is printed: no pepper, no key, no hash.
            var text = output + error.ToString();
            Assert.DoesNotContain(pepper, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestEncryption.KeyBase64, text, StringComparison.Ordinal);
            Assert.DoesNotContain("enc:v1:", text, StringComparison.Ordinal);
            foreach (var plain in new[] { "ABCD2345", "BCDF3456", "CDFG4567", "DFGH5678", "FGHJ6789" })
            {
                Assert.DoesNotContain(plain, sql, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

internal static class DictionaryExtensions
{
    public static Dictionary<string, string?> With(this Dictionary<string, string?> source, string key, string? value)
    {
        source[key] = value;
        return source;
    }
}

public class SeedSqlGeneratorTests
{
    private const string Pepper = "seed-test-pepper-seed-test-pepper-12";

    private static (SeedOutput Output, SecretHasher Hasher, AesGcmFieldProtector Protector) Generate()
    {
        var hasher = new SecretHasher(new AuthOptions { CodePepper = Pepper }, new PrivacyOptions { IpHashKey = new string('i', 40) });
        var protector = TestEncryption.Protector();
        var output = new SeedSqlGenerator(hasher, protector, new DocumentsOptions(), TimeProvider.System).Generate();
        return (output, hasher, protector);
    }

    [Fact]
    public void ItHoldsTheHashOfEachNewCode_AndNeverACodeOrThePepper()
    {
        var (output, hasher, _) = Generate();
        Assert.Equal(5, output.Codes.Count);
        foreach (var seed in output.Codes)
        {
            Assert.True(ParticipantCode.IsValid(seed.Code), "same format as a real code");
            Assert.DoesNotContain(seed.Code, output.Sql, StringComparison.Ordinal);
            Assert.Contains(hasher.HashCode(seed.Code), output.Sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Pepper, output.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRun_DrawsNewCodes_AndTheFormerFixedCodesAreGone()
    {
        var (first, _, _) = Generate();
        var (second, _, _) = Generate();
        Assert.NotEqual(first.Codes.Select(c => c.Code), second.Codes.Select(c => c.Code));
        Assert.Equal(5, first.Codes.Select(c => c.Code).Distinct().Count());
        foreach (var former in new[] { "ABCD2345", "BCDF3456", "CDFG4567", "DFGH5678", "FGHJ6789" })
        {
            Assert.DoesNotContain(former, first.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NicknamesAndMessageText_AreEncrypted_NeverPlain()
    {
        var (output, _, protector) = Generate();
        var sql = output.Sql;
        Assert.DoesNotContain("'Deneme'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeReplies.Pick("sakinleşmek", 0).Text, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Bugün kendimi çok yorgun", sql, StringComparison.Ordinal);
        Assert.Contains("'enc:v1:test:", sql, StringComparison.Ordinal);

        // and they are readable with the same key, bound to their row
        var nickname = System.Text.RegularExpressions.Regex.Match(sql, "'(enc:v1:test:[A-Za-z0-9+/=]+)'").Groups[1].Value;
        Assert.StartsWith("enc:v1:test:", nickname, StringComparison.Ordinal);
        Assert.Contains(sql.Split('\n'), l => l.Contains("'seed-2'", StringComparison.Ordinal) && l.Contains("enc:v1:test:", StringComparison.Ordinal));
        _ = protector;
    }

    [Fact]
    public void ItIsOneTransaction_AndWarnsThatTheDataIsSynthetic()
    {
        var (output, _, _) = Generate();
        var sql = output.Sql;
        Assert.Contains("BEGIN;", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT;", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT DO NOTHING", sql, StringComparison.Ordinal);
        Assert.Contains("DO UPDATE SET code_hash = EXCLUDED.code_hash, nickname = EXCLUDED.nickname", sql, StringComparison.Ordinal);
        Assert.Contains("SYNTHETIC DATA ONLY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereAreFiveParticipants_WithTheMockStates()
    {
        var (output, _, _) = Generate();
        foreach (var label in new[] { "'seed-1'", "'seed-2'", "'seed-3'", "'seed-4'", "'seed-5'" })
        {
            Assert.Contains(label, output.Sql, StringComparison.Ordinal);
        }

        Assert.Equal(["seed-1", "seed-2", "seed-3", "seed-4", "seed-5"], output.Codes.Select(c => c.Label));
        Assert.Contains("'Invited'", output.Sql, StringComparison.Ordinal);
        Assert.Contains("'Revoked'", output.Sql, StringComparison.Ordinal);
        Assert.Contains("'0.0-eski'", output.Sql, StringComparison.Ordinal); // the old versions
    }

    [Fact]
    public void TheFullParticipant_HasTwoFinishedConversations_AndNoActiveOne()
    {
        var (output, _, _) = Generate();
        var sql = output.Sql;
        var conversations = sql[sql.IndexOf("INSERT INTO public.conversations", StringComparison.Ordinal)..];
        conversations = conversations[..conversations.IndexOf("ON CONFLICT", StringComparison.Ordinal)];
        Assert.Equal(2, conversations.Split('\n').Count(l => l.TrimStart().StartsWith("('", StringComparison.Ordinal)));
        Assert.DoesNotContain(", NULL, 'Orientation')", conversations, StringComparison.Ordinal); // every one has an end time
    }

    [Fact]
    public void ReRunning_KeepsTheSameIds()
    {
        var (first, _, _) = Generate();
        var (second, _, _) = Generate();
        string Ids(string s) => string.Join("|", System.Text.RegularExpressions.Regex.Matches(s, "\\('[0-9a-f-]{36}'").Select(m => m.Value));
        Assert.Equal(Ids(first.Sql), Ids(second.Sql));
    }
}

public class FakeChatResponderTests
{
    private static async Task<List<string>> Run(FakeChatResponder responder, ConversationContext context, CancellationToken ct = default)
    {
        var chunks = new List<string>();
        await foreach (var chunk in responder.StreamReplyAsync(context, ct))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static FakeChatResponder Fast() => new(new ChatOptions { Fake = new FakeChatOptions { FirstTokenDelayMs = 0, TokenDelayMs = 0 } });

    private static ConversationContext Ctx(params (MessageRole Role, string Text)[] messages) =>
        new(messages.Select(m => new ContextMessage(m.Role, m.Text)).ToList(), Guid.NewGuid());

    [Fact]
    public async Task StreamsTheChosenTemplateWordByWord()
    {
        var chunks = await Run(Fast(), Ctx((MessageRole.User, "Çok korkuyorum")));
        Assert.True(chunks.Count > 10);
        Assert.Equal(FakeReplies.Pick("Çok korkuyorum", 0).Text, string.Concat(chunks));
    }

    [Fact]
    public async Task TheGeneralRepliesAlternate_ByCountingEarlierGeneralRepliesInTheContext()
    {
        const string text = "Bugün gün boyu pek bir şey yapamadım ve aklım dağınıktı";
        var first = await Run(Fast(), Ctx((MessageRole.User, text)));
        var general1 = FakeReplies.GeneralTemplates[0].Text;
        Assert.Equal(general1, string.Concat(first));

        var second = await Run(Fast(), Ctx(
            (MessageRole.User, text), (MessageRole.Assistant, general1), (MessageRole.User, text)));
        Assert.Equal(FakeReplies.GeneralTemplates[1].Text, string.Concat(second));
    }

    [Fact]
    public async Task ACancelledStream_StopsWithAnOperationCancelledException()
    {
        var slow = new FakeChatResponder(new ChatOptions { Fake = new FakeChatOptions { FirstTokenDelayMs = 5000, TokenDelayMs = 5000 } });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(slow, Ctx((MessageRole.User, "merhaba dünya nasılsın")), cts.Token));
    }

    [Fact]
    public async Task TheDelays_AreConfigurable_AndZeroInTests()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Run(Fast(), Ctx((MessageRole.User, "Çok korkuyorum")));
        Assert.True(watch.ElapsedMilliseconds < 500);

        var paced = new FakeChatResponder(new ChatOptions { Fake = new FakeChatOptions { FirstTokenDelayMs = 120, TokenDelayMs = 0 } });
        watch.Restart();
        await Run(paced, Ctx((MessageRole.User, "Çok korkuyorum")));
        Assert.True(watch.ElapsedMilliseconds >= 100);
    }
}
