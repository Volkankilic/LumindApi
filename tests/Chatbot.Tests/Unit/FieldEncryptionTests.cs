using System.Text.RegularExpressions;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Security;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Unit;

public class FieldProtectorTests
{
    private static readonly Guid Row = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ARoundTrip_ReturnsTheText_ForEveryKindOfText()
    {
        var protector = TestEncryption.Protector();
        foreach (var text in new[] { "merhaba", "", "çğıöşü ÇĞİÖŞÜ", "satır\nsonu\tve “tırnak”", new string('x', 5000), "enc:v1:test:literally typed by a user" })
        {
            var stored = protector.Protect("messages", "content", Row, text);
            Assert.StartsWith("enc:v1:test:", stored, StringComparison.Ordinal);
            Assert.Equal(text, protector.Unprotect("messages", "content", Row, stored));
        }
    }

    [Fact]
    public void TheStoredValue_HoldsNoPlainText_AndEveryWriteIsDifferent()
    {
        var protector = TestEncryption.Protector();
        var first = protector.Protect("messages", "content", Row, "kimseye söyleme");
        var second = protector.Protect("messages", "content", Row, "kimseye söyleme");
        Assert.DoesNotContain("söyleme", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second); // a random nonce per value
    }

    [Theory]
    [InlineData("participants", "content", "11111111-1111-1111-1111-111111111111")]
    [InlineData("messages", "nickname", "11111111-1111-1111-1111-111111111111")]
    [InlineData("messages", "content", "22222222-2222-2222-2222-222222222222")]
    public void ACiphertext_MovedToAnotherRowTableOrColumn_IsRefused(string table, string column, string row)
    {
        var protector = TestEncryption.Protector();
        var stored = protector.Protect("messages", "content", Row, "gizli");
        var ex = Assert.Throws<FieldProtectionException>(() => protector.Unprotect(table, column, Guid.Parse(row), stored));
        Assert.DoesNotContain("gizli", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATamperedCiphertext_IsRefused_ForEveryByteThatIsChanged()
    {
        var protector = TestEncryption.Protector();
        var stored = protector.Protect("messages", "content", Row, "gizli");
        var blobStart = stored.LastIndexOf(':') + 1;
        var blob = Convert.FromBase64String(stored[blobStart..]);
        for (var i = 0; i < blob.Length; i++)
        {
            var changed = (byte[])blob.Clone();
            changed[i] ^= 0x01;
            var tampered = stored[..blobStart] + Convert.ToBase64String(changed);
            Assert.Throws<FieldProtectionException>(() => protector.Unprotect("messages", "content", Row, tampered));
        }

        Assert.Throws<FieldProtectionException>(() => protector.Unprotect("messages", "content", Row, stored[..blobStart] + "###not-base64###"));
        Assert.Throws<FieldProtectionException>(() => protector.Unprotect("messages", "content", Row, stored[..blobStart] + "AAAA"));
    }

    [Fact]
    public void APlainTextValue_IsNeverTakenForPlainText_ItIsAnError()
    {
        var protector = TestEncryption.Protector();
        var ex = Assert.Throws<PlaintextFieldException>(() => protector.Unprotect("messages", "content", Row, "düz metin"));
        Assert.Contains(Row.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("düz metin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyId_IsRefused()
    {
        var stored = TestEncryption.Protector().Protect("messages", "content", Row, "gizli");
        var otherKeys = TestEncryption.Privacy(p =>
        {
            p.DataEncryptionKeys.Clear();
            p.DataEncryptionKeys[TestEncryption.OtherKeyId] = TestEncryption.OtherKeyBase64;
            p.CurrentDataEncryptionKeyId = TestEncryption.OtherKeyId;
        });
        Assert.Throws<FieldProtectionException>(() => TestEncryption.Protector(otherKeys).Unprotect("messages", "content", Row, stored));
    }

    [Fact]
    public void AfterAKeyChange_OldValuesStayReadable_AndNewOnesUseTheCurrentKey()
    {
        var oldProtector = TestEncryption.Protector();
        var oldValue = oldProtector.Protect("messages", "content", Row, "eski kayıt");

        var both = TestEncryption.Privacy(p =>
        {
            p.DataEncryptionKeys[TestEncryption.OtherKeyId] = TestEncryption.OtherKeyBase64;
            p.CurrentDataEncryptionKeyId = TestEncryption.OtherKeyId;
        });
        var rotated = TestEncryption.Protector(both);

        Assert.Equal("eski kayıt", rotated.Unprotect("messages", "content", Row, oldValue));
        var fresh = rotated.Protect("messages", "content", Row, "yeni kayıt");
        Assert.StartsWith($"enc:v1:{TestEncryption.OtherKeyId}:", fresh, StringComparison.Ordinal);
        Assert.Equal("yeni kayıt", rotated.Unprotect("messages", "content", Row, fresh));
    }
}

public class EncryptionKeyValidationTests
{
    private static IReadOnlyList<string> Problems(Action<PrivacyOptions> change)
    {
        var privacy = new PrivacyOptions();
        change(privacy);
        return OptionsValidator.ValidateEncryption(privacy);
    }

    [Fact]
    public void ValidKeys_AreAccepted() => Assert.Empty(OptionsValidator.ValidateEncryption(TestEncryption.Privacy()));

    [Fact]
    public void NoKeyAtAll_IsReportedByNameOfTheSetting()
    {
        var text = string.Join(" ", Problems(_ => { }));
        Assert.Contains("Privacy:DataEncryptionKeys", text, StringComparison.Ordinal);
        Assert.Contains("Privacy:CurrentDataEncryptionKeyId", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")] // base64, but 3 bytes
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")] // 31 bytes (one short)
    public void ABadKey_IsRefused_AndItsValueNeverAppearsInTheMessage(string value)
    {
        var problems = Problems(p =>
        {
            p.DataEncryptionKeys["k1"] = value;
            p.CurrentDataEncryptionKeyId = "k1";
        });
        var text = string.Join(" ", problems);
        Assert.Contains("Privacy:DataEncryptionKeys:k1", text, StringComparison.Ordinal);
        if (value.Length > 3)
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACurrentIdThatNamesNoKey_IsRefused()
    {
        var problems = Problems(p =>
        {
            p.DataEncryptionKeys["k1"] = TestEncryption.KeyBase64;
            p.CurrentDataEncryptionKeyId = "k2";
        });
        Assert.Contains(problems, x => x.Contains("Privacy:CurrentDataEncryptionKeyId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("has:colon")]
    [InlineData("white space")]
    [InlineData("")]
    public void AKeyIdThatCouldBreakTheStoredPrefix_IsRefused(string id) => Assert.False(OptionsValidator.IsValidKeyId(id));

    [Fact]
    public async Task TheApplicationRefusesToStart_WithoutAKey_InEveryEnvironment_AndTheMessageHasNoValue()
    {
        foreach (var environment in new[] { "Production", "Development", "Testing" })
        {
            // The settings of the test host are merged over the defaults, so the key is blanked rather than removed.
            var env = environment;
            var text = await FailedStart.TextAsync(() => new ApiFactory(
                environment: env,
                settings: new()
                {
                    [$"Privacy:DataEncryptionKeys:{TestEncryption.KeyId}"] = "",
                    ["Privacy:CurrentDataEncryptionKeyId"] = "",
                    ["Auth:CookieSecure"] = "true",
                }));
            Assert.True(text.Contains("Privacy:DataEncryptionKeys", StringComparison.Ordinal), $"{environment}: {text[..Math.Min(text.Length, 1500)]}");
            Assert.DoesNotContain(TestEncryption.KeyBase64, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AProductionHost_WithAValidKey_Starts()
    {
        using var factory = new ApiFactory(environment: "Production", settings: new() { ["Auth:CookieSecure"] = "true" });
        using var client = factory.CreateClient();
        Assert.NotNull(client);
    }
}

public class FieldEncryptionInterceptorTests
{
    private static Message NewMessage(string text, Guid? conversationId = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        ConversationId = conversationId ?? Guid.CreateVersion7(),
        Role = MessageRole.User,
        Content = text,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>The value exactly as the database holds it: read through a context WITHOUT the interceptor.</summary>
    private static async Task<string> RawContentAsync(string databaseName, Guid id)
    {
        await using var raw = TestEncryption.RawInMemoryContext(databaseName);
        return (await raw.Messages.AsNoTracking().SingleAsync(m => m.Id == id)).Content;
    }

    [Fact]
    public async Task WhatIsStored_IsCiphertext_AndWhatIsRead_IsPlainText()
    {
        var name = Guid.NewGuid().ToString("N");
        var message = NewMessage("bugün çok yorgunum");
        await using (var db = TestEncryption.InMemoryContext(name))
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync();
        }

        var raw = await RawContentAsync(name, message.Id);
        Assert.StartsWith("enc:v1:test:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("yorgun", raw, StringComparison.Ordinal);

        await using var read = TestEncryption.InMemoryContext(name);
        Assert.Equal("bugün çok yorgunum", (await read.Messages.SingleAsync(m => m.Id == message.Id)).Content);
        Assert.Equal("bugün çok yorgunum", (await read.Messages.AsNoTracking().SingleAsync(m => m.Id == message.Id)).Content);
    }

    [Fact]
    public async Task TheTrackedEntity_StaysPlain_AndConsistent_AfterTheSave_WithNoDoubleEncryption()
    {
        var name = Guid.NewGuid().ToString("N");
        var message = NewMessage("ilk metin");
        await using var db = TestEncryption.InMemoryContext(name);
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        // Same context, right after the save: the entity holds plain text and is not seen as changed.
        Assert.Equal("ilk metin", message.Content);
        var entry = db.Entry(message);
        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.False(entry.Property(m => m.Content).IsModified);
        Assert.Equal("ilk metin", (string)entry.Property(m => m.Content).OriginalValue!);

        // Reading it back in the same context gives the same plain text (the tracked instance).
        Assert.Equal("ilk metin", (await db.Messages.SingleAsync(m => m.Id == message.Id)).Content);

        // Saving again with nothing changed must not write (and so must not encrypt again).
        var before = await RawContentAsync(name, message.Id);
        await db.SaveChangesAsync();
        Assert.Equal(before, await RawContentAsync(name, message.Id));
        Assert.Equal("ilk metin", message.Content);

        // Another property changes: the content column is not rewritten.
        message.IsCrisis = true;
        await db.SaveChangesAsync();
        Assert.Equal(before, await RawContentAsync(name, message.Id));
        Assert.Equal("ilk metin", message.Content);
    }

    [Fact]
    public async Task ChangingTheText_EncryptsTheNewValue_ExactlyOnce()
    {
        var name = Guid.NewGuid().ToString("N");
        var message = NewMessage("önce");
        await using var db = TestEncryption.InMemoryContext(name);
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        var first = await RawContentAsync(name, message.Id);

        message.Content = "sonra";
        await db.SaveChangesAsync();

        var second = await RawContentAsync(name, message.Id);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("sonra", second, StringComparison.Ordinal);
        Assert.Equal("sonra", message.Content);
        await using var read = TestEncryption.InMemoryContext(name);
        Assert.Equal("sonra", (await read.Messages.SingleAsync(m => m.Id == message.Id)).Content);
    }

    [Fact]
    public async Task ASaveThatFails_PutsThePlainTextBack_SoARetryEncryptsAfresh()
    {
        var name = Guid.NewGuid().ToString("N");
        var id = Guid.CreateVersion7();
        await using (var seed = TestEncryption.InMemoryContext(name))
        {
            seed.Messages.Add(NewMessage("vardı", id: id));
            await seed.SaveChangesAsync();
        }

        await using var db = TestEncryption.InMemoryContext(name);
        var duplicate = NewMessage("çakışan metin", id: id); // same key: the save fails
        db.Messages.Add(duplicate);
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());

        Assert.Equal("çakışan metin", duplicate.Content);
        Assert.Equal(EntityState.Added, db.Entry(duplicate).State);
    }

    [Fact]
    public async Task ANickname_IsEncrypted_ANullOneStaysNull_AndAnUnchangedOneIsNotRewritten()
    {
        var name = Guid.NewGuid().ToString("N");
        var withNick = new Participant { Id = Guid.CreateVersion7(), CodeHash = "h1", Status = ParticipantStatus.Active, Nickname = "Ayşe", CreatedAt = DateTimeOffset.UtcNow };
        var without = new Participant { Id = Guid.CreateVersion7(), CodeHash = "h2", Status = ParticipantStatus.Active, CreatedAt = DateTimeOffset.UtcNow };
        await using (var db = TestEncryption.InMemoryContext(name))
        {
            db.Participants.AddRange(withNick, without);
            await db.SaveChangesAsync();
            Assert.Equal("Ayşe", withNick.Nickname);
            Assert.Null(without.Nickname);

            withNick.LastSeenAt = DateTimeOffset.UtcNow; // not the nickname
            await db.SaveChangesAsync();
            Assert.Equal("Ayşe", withNick.Nickname);
        }

        await using (var raw = TestEncryption.RawInMemoryContext(name))
        {
            var stored = await raw.Participants.AsNoTracking().OrderBy(p => p.CodeHash).ToListAsync();
            Assert.StartsWith("enc:v1:test:", stored[0].Nickname, StringComparison.Ordinal);
            Assert.DoesNotContain("Ayşe", stored[0].Nickname, StringComparison.Ordinal);
            Assert.Null(stored[1].Nickname);
        }

        await using var read = TestEncryption.InMemoryContext(name);
        var back = await read.Participants.AsNoTracking().OrderBy(p => p.CodeHash).ToListAsync();
        Assert.Equal("Ayşe", back[0].Nickname);
        Assert.Null(back[1].Nickname);
    }

    [Fact]
    public async Task ARowThatHoldsPlainText_FailsToLoad_AndTheLogNamesTheRowButNeverTheText()
    {
        var name = Guid.NewGuid().ToString("N");
        var legacy = NewMessage("SIFRESIZ-ESKI-METIN");
        await using (var raw = TestEncryption.RawInMemoryContext(name))
        {
            raw.Messages.Add(legacy); // no interceptor: written as plain text, like a row from before encryption
            await raw.SaveChangesAsync();
        }

        var logs = new List<string>();
        var interceptor = new FieldEncryptionInterceptor(TestEncryption.Protector(), new ListLogger<FieldEncryptionInterceptor>(logs));
        await using var db = TestEncryption.InMemoryContext(name, interceptor);
        var ex = await Assert.ThrowsAsync<PlaintextFieldException>(() => db.Messages.SingleAsync(m => m.Id == legacy.Id));

        Assert.Contains(legacy.Id.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SIFRESIZ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(logs, l => l.Contains(legacy.Id.ToString(), StringComparison.Ordinal) && l.Contains("not encrypted", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("SIFRESIZ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACiphertextCopiedToAnotherRow_FailsToLoad()
    {
        var name = Guid.NewGuid().ToString("N");
        var a = NewMessage("a'nın metni");
        var b = NewMessage("b'nin metni");
        await using (var db = TestEncryption.InMemoryContext(name))
        {
            db.Messages.AddRange(a, b);
            await db.SaveChangesAsync();
        }

        var aRaw = await RawContentAsync(name, a.Id);
        await using (var raw = TestEncryption.RawInMemoryContext(name))
        {
            (await raw.Messages.SingleAsync(m => m.Id == b.Id)).Content = aRaw; // swap a's ciphertext into b's row
            await raw.SaveChangesAsync();
        }

        await using var read = TestEncryption.InMemoryContext(name);
        await Assert.ThrowsAsync<FieldProtectionException>(() => read.Messages.SingleAsync(m => m.Id == b.Id));
        Assert.Equal("a'nın metni", (await read.Messages.SingleAsync(m => m.Id == a.Id)).Content);
    }

    [Fact]
    public async Task AnAuditRow_CanBeAdded_ButNeverChangedOrDeletedThroughTheContext()
    {
        var name = Guid.NewGuid().ToString("N");
        var row = new AuditLog { Id = Guid.CreateVersion7(), ActorType = ActorType.Participant, Action = "test.event", Timestamp = DateTimeOffset.UtcNow };
        await using var db = TestEncryption.InMemoryContext(name);
        db.AuditLogs.Add(row);
        await db.SaveChangesAsync();

        row.Action = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await using var other = TestEncryption.InMemoryContext(name);
        var tracked = await other.AuditLogs.SingleAsync(a => a.Id == row.Id);
        Assert.Equal("test.event", tracked.Action);
        other.AuditLogs.Remove(tracked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
    }

    private sealed class ListLogger<T>(List<string> lines) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
    }
}

/// <summary>
/// The interceptor covers entities that are loaded and saved through the context. These paths BYPASS it, so the source
/// must not contain them for message content or the nickname: a projection of the field, an ExecuteUpdate that writes it,
/// and raw SQL. (Nulling the nickname with ExecuteUpdate is fine: NULL is not encrypted.)
/// </summary>
public class BypassPathGuardTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Chatbot.sln not found"), "src");
    }

    private static IEnumerable<(string Path, string[] Lines)> Sources() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (f, File.ReadAllLines(f)));

    [Fact]
    public void NoQueryProjectsTheContentOrTheNickname()
    {
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            // ChatRules.BuildContext maps messages that are ALREADY loaded (and decrypted): an in-memory projection, not a query.
            if (System.IO.Path.GetFileName(path) == "ChatRules.cs")
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"\.Select(Many)?\(") && Regex.IsMatch(line, @"\b\w+\.(Content|Nickname|TotpSecret)\b"))
                {
                    offenders.Add($"{System.IO.Path.GetFileName(path)}:{i + 1}");
                }

                if (Regex.IsMatch(line, @"=>\s*new\b") && Regex.IsMatch(line, @"\b\w+\.(Content|Nickname|TotpSecret)\b") && !Regex.IsMatch(line, @"\bMessageDto\b"))
                {
                    offenders.Add($"{System.IO.Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "A projection skips decryption: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoExecuteUpdateWritesTheContentOrAValueIntoTheNickname()
    {
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("SetProperty", StringComparison.Ordinal) || !Regex.IsMatch(line, @"\.(Content|Nickname)\b"))
                {
                    continue;
                }

                var nullsTheNickname = Regex.IsMatch(line, @"\.Nickname\s*,\s*\(string\?\)null\)");
                if (!nullsTheNickname)
                {
                    offenders.Add($"{System.IO.Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "ExecuteUpdate would write unencrypted text: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RawSql_IsOnlyUsedWhereItNeverTouchesTheEncryptedFields()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "RetentionService.cs", "SchemaStatusProvider.cs" };
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            var text = string.Join('\n', lines);
            if (!Regex.IsMatch(text, @"\b(FromSql|FromSqlRaw|FromSqlInterpolated|ExecuteSql|ExecuteSqlRaw|ExecuteSqlInterpolated|SqlQuery|NpgsqlCommand)\b"))
            {
                continue;
            }

            var file = System.IO.Path.GetFileName(path);
            if (!allowed.Contains(file))
            {
                offenders.Add(file);
                continue;
            }

            // In the allowed files, no SQL text may name the encrypted tables or columns.
            var sqlText = Regex.Matches(text, @"""((?:[^""\\]|\\.)*)""")
                .Select(m => m.Groups[1].Value)
                .Where(s => Regex.IsMatch(s, @"\b(SELECT|UPDATE|INSERT|DELETE|FROM)\b"));
            if (sqlText.Any(s => Regex.IsMatch(s, @"\b(messages|nickname|content)\b", RegexOptions.IgnoreCase)))
            {
                offenders.Add(file);
            }
        }

        Assert.True(offenders.Count == 0, "Raw SQL bypasses the encryption interceptor: " + string.Join(", ", offenders));
    }

    [Fact]
    public void AuditRows_AreOnlyDeletedByTheRetentionJob_AndTheDevelopmentTestCleanup()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "RetentionService.cs", "DevTestParticipantService.cs", "DevTestAdminService.cs" };
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            var file = System.IO.Path.GetFileName(path);
            var text = string.Join('\n', lines);
            var touchesAuditRows = Regex.IsMatch(text, @"AuditLogs\s*\.\s*(Where|Remove|RemoveRange|ExecuteDelete|ExecuteUpdate)");
            if (touchesAuditRows && !allowed.Contains(file))
            {
                offenders.Add(file);
            }
        }

        Assert.True(offenders.Count == 0, "Audit rows are changed or deleted outside the allowed code: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryAuditWrite_UsesANameFromTheOneList_NeverALooseString()
    {
        var offenders = new List<string>();
        foreach (var (path, lines) in Sources())
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("audit.WriteAsync(", StringComparison.Ordinal))
                {
                    continue;
                }

                // the action is either a constant of AuditActions or the local `action` built by AuditActions.Consent(...)
                if (!line.Contains("AuditActions.", StringComparison.Ordinal) && !Regex.IsMatch(line, @",\s*action\s*,"))
                {
                    offenders.Add($"{System.IO.Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "An audit event name that is not in AuditActions: " + string.Join(", ", offenders));
        Assert.Equal(Chatbot.Domain.AuditActions.All.Count, Chatbot.Domain.AuditActions.All.Distinct().Count());
    }

    [Fact]
    public void DevTestCleanup_IsOnlyRegisteredInDevelopment_AndRefusesRowsThatAreNotTestRows()
    {
        // The one allowed audit deleter besides the retention job is not even registered outside Development (see ADR 004/006).
        using var provider = TestServices.Build("Production");
        Assert.Null(provider.GetService(typeof(Chatbot.Infrastructure.Dev.DevTestParticipantService)));
    }
}
