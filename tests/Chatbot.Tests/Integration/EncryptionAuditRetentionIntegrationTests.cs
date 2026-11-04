using System.Net;
using System.Text.RegularExpressions;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Retention;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Chatbot.Tests.Integration;

internal static class RawSql
{
    public static async Task<List<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    public static async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Message content and nicknames reach the REAL database only as ciphertext (REQUIRE_DB_TESTS=1 makes this mandatory).</summary>
public class EncryptionIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    [PostgresFact]
    public async Task WhatReachesTheDatabase_IsCiphertext_AndTheApiStillReturnsPlainText()
    {
        const string nickname = "SifreliTakmaAd77";
        const string userText = "GIZLI-ICERIK-ALPHA bugün çok yorgunum";
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);
        await session.OnboardAsync(nickname);
        var conversation = await session.StartConversationAsync();
        var (response, events) = await session.SendAsync(conversation, userText);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = events.AssembleReply();
        Assert.NotEmpty(reply);

        // The raw column, read with plain SQL: no plain text anywhere.
        var messages = await RawSql.RowsAsync(
            "SELECT role, content FROM public.messages WHERE conversation_id = @c ORDER BY created_at",
            ("c", conversation));
        Assert.Equal(2, messages.Count);
        foreach (var row in messages)
        {
            var content = (string)row[1]!;
            Assert.StartsWith("enc:v1:test:", content, StringComparison.Ordinal);
            Assert.DoesNotContain("GIZLI-ICERIK", content, StringComparison.Ordinal);
            Assert.DoesNotContain("yorgun", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(reply[..Math.Min(15, reply.Length)], content, StringComparison.Ordinal);
        }

        var nicknameRow = await RawSql.RowsAsync("SELECT nickname FROM public.participants WHERE id = @p", ("p", participant.Id));
        var storedNickname = (string)nicknameRow.Single()[0]!;
        Assert.StartsWith("enc:v1:test:", storedNickname, StringComparison.Ordinal);
        Assert.DoesNotContain(nickname, storedNickname, StringComparison.Ordinal);

        // ... and the API still answers with plain text.
        var detail = await (await session.Client.GetAsync($"/api/conversations/{conversation}")).ReadJsonAsync();
        var texts = detail.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToList();
        Assert.Equal([userText, reply], texts);
        var me = await (await session.Client.GetAsync("/api/me")).ReadJsonAsync();
        Assert.Equal(nickname, me.GetProperty("nickname").GetString());
    }

    [PostgresFact]
    public async Task InTheSameContext_ReadingAfterTheSave_GivesPlainText_AndTheRowIsStillCiphertext()
    {
        var participant = await host.CreateParticipantAsync(ParticipantStatus.Active);
        var conversationId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        await using var db = TestDatabase.NewContext();
        var tracked = await db.Participants.SingleAsync(p => p.Id == participant.Id);
        tracked.Nickname = "ContextAd";
        db.Conversations.Add(new Conversation { Id = conversationId, ParticipantId = participant.Id, StartedAt = DateTimeOffset.UtcNow });
        db.Messages.Add(new Message { Id = messageId, ConversationId = conversationId, Role = MessageRole.User, Content = "baglam-metni", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.Equal("ContextAd", tracked.Nickname);
        Assert.Equal("baglam-metni", (await db.Messages.SingleAsync(m => m.Id == messageId)).Content);
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);

        await db.SaveChangesAsync(); // nothing changed: must not touch the columns
        var raw = (string)(await RawSql.RowsAsync("SELECT content FROM public.messages WHERE id = @m", ("m", messageId))).Single()[0]!;
        Assert.StartsWith("enc:v1:test:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("baglam-metni", raw, StringComparison.Ordinal);
        await using var fresh = TestDatabase.NewContext();
        Assert.Equal("baglam-metni", (await fresh.Messages.SingleAsync(m => m.Id == messageId)).Content);
    }

    [PostgresFact]
    public async Task ARowThatWasNeverEncrypted_IsAnError_NeverServedAsPlainText_AndTheLogHasNoContent()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);
        await session.OnboardAsync();
        var conversation = await session.StartConversationAsync();
        var legacyId = Guid.CreateVersion7();
        await RawSql.ExecuteAsync(
            "INSERT INTO public.messages (id, conversation_id, role, content, created_at, is_crisis) VALUES (@id, @c, 'User', 'SIFRESIZ-ESKI-METIN', now(), false)",
            ("id", legacyId),
            ("c", conversation));

        var response = await session.Client.GetAsync($"/api/conversations/{conversation}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SIFRESIZ", body, StringComparison.Ordinal);
        var logs = host.Factory.Logs.Rendered().ToList();
        Assert.Contains(logs, l => l.Contains(legacyId.ToString(), StringComparison.Ordinal) && l.Contains("not encrypted", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("SIFRESIZ", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task AParticipantWhoseNicknameIsPlainText_CannotSignIn_UntilTheSyntheticDataIsReset()
    {
        var participant = await host.CreateParticipantAsync(ParticipantStatus.Active);
        await RawSql.ExecuteAsync("UPDATE public.participants SET nickname = 'eski-takma-ad' WHERE id = @p", ("p", participant.Id));

        var (response, _) = await host.Factory.PostCodeAsync(participant.Code);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("eski-takma-ad", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

/// <summary>The audit trail: what is written, that it holds no content, and that the application cannot change or delete it.</summary>
public class AuditIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    [PostgresFact]
    public async Task EveryStepOfAConversation_LeavesAnAuditRow_ReadingTheHistoryIncluded_AndNothingElseThanTheKnownEvents()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);
        await session.OnboardAsync("AuditAd");
        var conversation = await session.StartConversationAsync();
        await session.SendAsync(conversation, "denetim deneme metni");
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"/api/conversations/{conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync($"/api/conversations/{conversation}/end", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync($"/api/conversations/{conversation}/end", content: null)).StatusCode); // idempotent

        var rows = await RawSql.RowsAsync(
            "SELECT action, target_type, target_id, ip_hash FROM public.audit_logs WHERE actor_id = @p",
            ("p", participant.Id));
        var actions = rows.Select(r => (string)r[0]!).ToList();

        foreach (var expected in new[]
        {
            AuditActions.AuthCodeSucceeded, AuditActions.ConversationStarted, AuditActions.HistoryListed,
            AuditActions.ConversationRead, AuditActions.ConversationEnded, AuditActions.ParticipantNicknameSet,
            AuditActions.ConsentNoticeGranted, AuditActions.ConsentExplicitGranted,
        })
        {
            Assert.Contains(expected, actions);
        }

        Assert.Single(actions, a => a == AuditActions.ConversationEnded); // the second end changed nothing, so it is not logged again
        Assert.All(actions, a => Assert.Contains(a, AuditActions.All));
        Assert.All(rows, r =>
        {
            Assert.True(r[3] is null || Regex.IsMatch((string)r[3]!, "^[0-9a-f]{64}$"), "an IP is stored only as a keyed hash");
        });

        var read = rows.Single(r => (string)r[0]! == AuditActions.ConversationRead);
        Assert.Equal(nameof(Conversation), read[1]);
        Assert.Equal(conversation, (Guid)read[2]!);
    }

    [PostgresFact]
    public async Task AnAuditRow_CannotBeChangedOrDeleted_ThroughTheApplicationsContext()
    {
        var participant = await host.CreateParticipantAsync();
        var row = new AuditLog { Id = Guid.CreateVersion7(), ActorType = ActorType.Participant, ActorId = participant.Id, Action = "itest.audit.immutable", Timestamp = DateTimeOffset.UtcNow };
        await using (var db = TestDatabase.NewContext())
        {
            db.AuditLogs.Add(row);
            await db.SaveChangesAsync();
        }

        await using (var db = TestDatabase.NewContext())
        {
            var tracked = await db.AuditLogs.SingleAsync(a => a.Id == row.Id);
            tracked.Action = "tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = TestDatabase.NewContext())
        {
            db.AuditLogs.Remove(await db.AuditLogs.SingleAsync(a => a.Id == row.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        var stored = await RawSql.RowsAsync("SELECT action FROM public.audit_logs WHERE id = @id", ("id", row.Id));
        Assert.Equal("itest.audit.immutable", stored.Single()[0]);
    }
}

public sealed class RecordingAudit : IAuditWriter
{
    public List<(ActorType Actor, string Action)> Calls { get; } = [];

    public Task WriteAsync(ActorType actorType, Guid? actorId, string action, string? targetType, Guid? targetId, string? clientIp, CancellationToken ct)
    {
        Calls.Add((actorType, action));
        return Task.CompletedTask;
    }
}

/// <summary>
/// The retention job against the real database, narrowed to `itest-` participants and `itest.retention.` audit rows
/// (the seed participants and everybody else's rows are never in scope). One class, so the advisory lock test cannot meet another run.
/// </summary>
public class RetentionIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    // A new class instance is created per test, so every test has audit actions of its own (tests share one database).
    private readonly string AuditPrefix = $"itest.retention.{Guid.NewGuid():N}.";
    private RetentionScope Scope => new("itest-", AuditPrefix, WriteAudit: false);

    private static PrivacyOptions Options(bool dryRun, int? conversations = 30, int? anonymous = 30, int? audit = 30, bool tokens = true) =>
        TestEncryption.Privacy(p =>
        {
            p.RetentionDays.EndedConversations = conversations;
            p.RetentionDays.AnonymousAudit = anonymous;
            p.RetentionDays.AuditLogs = audit;
            p.Retention.ExpiredRefreshTokens = tokens;
            p.Retention.DryRun = dryRun;
            p.Retention.BatchSize = 2; // small, so more than one batch is needed
        });

    private static RetentionService Service(AppDbContext db, PrivacyOptions privacy, IAuditWriter? audit = null) =>
        new(db, privacy, TimeProvider.System, audit ?? new RecordingAudit(), NullLogger<RetentionService>.Instance);

    private sealed record Data(Guid Old, Guid Recent, Guid Abandoned, Guid Active, TestParticipant Owner);

    private async Task<Data> ArrangeAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = await host.CreateParticipantAsync(ParticipantStatus.Active);
        var abandoned = await host.CreateParticipantAsync(ParticipantStatus.Active);
        var active = await host.CreateParticipantAsync(ParticipantStatus.Active);
        await using var db = TestDatabase.NewContext();

        Guid Conversation(TestParticipant who, DateTimeOffset started, DateTimeOffset? ended, DateTimeOffset firstMessage, int messages = 2)
        {
            var id = Guid.CreateVersion7();
            db.Conversations.Add(new Conversation { Id = id, ParticipantId = who.Id, StartedAt = started, EndedAt = ended });
            for (var i = 0; i < messages; i++)
            {
                db.Messages.Add(new Message
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = id,
                    Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                    Content = $"saklama-deneme-{i}",
                    CreatedAt = firstMessage.AddMinutes(i),
                });
            }

            return id;
        }

        var old = Conversation(owner, now.AddDays(-101), now.AddDays(-100), now.AddDays(-101));
        var recent = Conversation(owner, now.AddDays(-3), now.AddDays(-2), now.AddDays(-3));
        var abandonedOld = Conversation(abandoned, now.AddDays(-101), null, now.AddDays(-100)); // never ended, last message 100 days ago
        var activeRecent = Conversation(active, now.AddDays(-1), null, now.AddDays(-1));

        void Audit(ActorType type, Guid? actor, string name, DateTimeOffset at) =>
            db.AuditLogs.Add(new AuditLog { Id = Guid.CreateVersion7(), ActorType = type, ActorId = actor, Action = AuditPrefix + name, Timestamp = at });

        Audit(ActorType.Anonymous, null, "anon.old", now.AddDays(-100));
        Audit(ActorType.Anonymous, null, "anon.new", now.AddDays(-1));
        Audit(ActorType.Participant, owner.Id, "part.old", now.AddDays(-100));
        Audit(ActorType.Participant, owner.Id, "part.new", now.AddDays(-1));

        void Token(string name, DateTimeOffset expires) =>
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.CreateVersion7(),
                ParticipantId = owner.Id,
                TokenHash = $"itest-{name}-{Guid.NewGuid():N}",
                FamilyId = Guid.CreateVersion7(),
                CreatedAt = now.AddDays(-30),
                ExpiresAt = expires,
                FamilyExpiresAt = expires,
            });
        Token("expired", now.AddDays(-5));
        Token("valid", now.AddDays(5));

        await db.SaveChangesAsync();
        return new Data(old, recent, abandonedOld, activeRecent, owner);
    }

    private static async Task<bool> ConversationExistsAsync(Guid id) =>
        (await RawSql.RowsAsync("SELECT 1 FROM public.conversations WHERE id = @id", ("id", id))).Count == 1;

    private static async Task<int> MessagesOfAsync(Guid conversation) =>
        (int)(long)(await RawSql.RowsAsync("SELECT count(*) FROM public.messages WHERE conversation_id = @id", ("id", conversation))).Single()[0]!;

    private async Task<List<string>> AuditNamesAsync() =>
        (await RawSql.RowsAsync("SELECT action FROM public.audit_logs WHERE action LIKE @p ORDER BY action", ("p", AuditPrefix + "%")))
        .Select(r => (string)r[0]!).ToList();

    private static async Task<int> TokensOfAsync(Guid participant) =>
        (int)(long)(await RawSql.RowsAsync("SELECT count(*) FROM public.refresh_tokens WHERE participant_id = @p", ("p", participant))).Single()[0]!;

    private Task RemoveAnonymousLeftoversAsync() =>
        RawSql.ExecuteAsync("DELETE FROM public.audit_logs WHERE action LIKE @p AND actor_id IS NULL", ("p", AuditPrefix + "%"));

    [PostgresFact]
    public async Task ADryRun_CountsWhatWouldGo_AndDeletesNothing()
    {
        var data = await ArrangeAsync();
        try
        {
            await using var db = TestDatabase.NewContext();
            var result = await Service(db, Options(dryRun: true)).RunAsync(Scope, dryRun: null, CancellationToken.None);

            Assert.True(result.DryRun);
            Assert.True(result.Conversations >= 2); // the old ended one and the abandoned one
            Assert.True(result.AnonymousAudit >= 1);
            Assert.True(result.AuditLogs >= 1);
            Assert.True(result.RefreshTokens >= 1);

            Assert.True(await ConversationExistsAsync(data.Old));
            Assert.Equal(2, await MessagesOfAsync(data.Old));
            Assert.True(await ConversationExistsAsync(data.Abandoned));
            Assert.Equal(4, (await AuditNamesAsync()).Count);
            Assert.Equal(2, await TokensOfAsync(data.Owner.Id));
        }
        finally
        {
            await RemoveAnonymousLeftoversAsync();
        }
    }

    [PostgresFact]
    public async Task ARealRun_DeletesOnlyWhatIsOldEnough_InBatches_AndKeepsEverythingElse()
    {
        var data = await ArrangeAsync();
        var audit = new RecordingAudit();
        try
        {
            await using var db = TestDatabase.NewContext();
            var result = await Service(db, Options(dryRun: false), audit).RunAsync(new RetentionScope("itest-", AuditPrefix, WriteAudit: true), dryRun: null, CancellationToken.None);

            Assert.False(result.DryRun);
            Assert.False(result.SkippedBecauseLocked);

            // conversations: counted from the end, or from the last message when never ended; their messages go with them
            Assert.False(await ConversationExistsAsync(data.Old));
            Assert.Equal(0, await MessagesOfAsync(data.Old));
            Assert.False(await ConversationExistsAsync(data.Abandoned));
            Assert.Equal(0, await MessagesOfAsync(data.Abandoned));
            Assert.True(await ConversationExistsAsync(data.Recent));
            Assert.Equal(2, await MessagesOfAsync(data.Recent));
            Assert.True(await ConversationExistsAsync(data.Active));

            // audit: the anonymous rule and the other rule are separate
            Assert.Equal([AuditPrefix + "anon.new", AuditPrefix + "part.new"], await AuditNamesAsync());

            // refresh tokens: the expired one goes, the valid one stays
            Assert.Equal(1, await TokensOfAsync(data.Owner.Id));

            // the run itself is written to the audit log, by the system, once
            Assert.Equal([(ActorType.System, AuditActions.RetentionRun)], audit.Calls);
        }
        finally
        {
            await RemoveAnonymousLeftoversAsync();
        }
    }

    [PostgresFact]
    public async Task EveryRuleIsOff_UnlessConfigured_SoANormalConfigurationDeletesNothing()
    {
        var data = await ArrangeAsync();
        try
        {
            var none = Options(dryRun: false, conversations: null, anonymous: null, audit: null, tokens: false);
            Assert.False(none.RetentionDays.AnyEnabled);
            await using var db = TestDatabase.NewContext();
            var service = Service(db, none);
            Assert.False(service.AnythingEnabled);

            var result = await service.RunAsync(Scope, dryRun: null, CancellationToken.None);

            Assert.Equal(0, result.Total);
            Assert.True(await ConversationExistsAsync(data.Old));
            Assert.Equal(4, (await AuditNamesAsync()).Count);
            Assert.Equal(2, await TokensOfAsync(data.Owner.Id));

            // zero is "off" too
            var zero = Options(dryRun: false, conversations: 0, anonymous: 0, audit: 0, tokens: false);
            Assert.False(zero.RetentionDays.AnyEnabled);
            Assert.Equal(0, (await Service(db, zero).RunAsync(Scope, dryRun: null, CancellationToken.None)).Total);
        }
        finally
        {
            await RemoveAnonymousLeftoversAsync();
        }
    }

    [PostgresFact]
    public async Task OnlyOneInstanceRunsAtATime_AnotherOneSkipsWhileTheLockIsHeld()
    {
        var data = await ArrangeAsync();
        await using var holder = new NpgsqlConnection(TestDatabase.ConnectionString);
        await holder.OpenAsync();
        try
        {
            await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", holder))
            {
                take.Parameters.AddWithValue("k", RetentionService.LockKey);
                await take.ExecuteScalarAsync();
            }

            await using var db = TestDatabase.NewContext();
            var result = await Service(db, Options(dryRun: false)).RunAsync(Scope, dryRun: null, CancellationToken.None);

            Assert.True(result.SkippedBecauseLocked);
            Assert.Equal(0, result.Total);
            Assert.True(await ConversationExistsAsync(data.Old));

            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", holder))
            {
                release.Parameters.AddWithValue("k", RetentionService.LockKey);
                await release.ExecuteScalarAsync();
            }

            await using var again = TestDatabase.NewContext();
            var second = await Service(again, Options(dryRun: false)).RunAsync(Scope, dryRun: null, CancellationToken.None);
            Assert.False(second.SkippedBecauseLocked);
            Assert.False(await ConversationExistsAsync(data.Old));
        }
        finally
        {
            await RemoveAnonymousLeftoversAsync();
        }
    }

    [PostgresFact]
    public async Task AConversationDeletedByRetention_IsAnOrdinary404ForItsOwner_AndTheHistoryKeepsWorking()
    {
        var data = await ArrangeAsync();
        try
        {
            var session = await host.Factory.SignInAsync(data.Owner);
            await session.OnboardAsync();

            var before = await session.Client.GetAsync($"/api/conversations/{data.Old}");
            Assert.True(
                before.StatusCode == HttpStatusCode.OK,
                $"{before.StatusCode}; last log lines: " + string.Join(" | ", host.Factory.Logs.Rendered().TakeLast(4).Select(l => l[..Math.Min(l.Length, 240)])));

            await using (var db = TestDatabase.NewContext())
            {
                await Service(db, Options(dryRun: false, anonymous: null, audit: null, tokens: false)).RunAsync(Scope, dryRun: null, CancellationToken.None);
            }

            var gone = await session.Client.GetAsync($"/api/conversations/{data.Old}");
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Equal("not-found", (await gone.ReadJsonAsync()).GetProperty("code").GetString());

            var list = await (await session.Client.GetAsync("/api/conversations")).ReadJsonAsync();
            var ids = list.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(data.Recent, ids);
            Assert.DoesNotContain(data.Old, ids);
            Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"/api/conversations/{data.Recent}")).StatusCode);
        }
        finally
        {
            await RemoveAnonymousLeftoversAsync();
        }
    }

    [PostgresFact]
    public async Task TheSeedParticipants_AreOutOfScope_EvenWhenTheirDataIsOld()
    {
        // The scope names `itest-` participants only: a seed-like row with an old conversation is left alone.
        var now = DateTimeOffset.UtcNow;
        var seedLike = Guid.CreateVersion7();
        var conversation = Guid.CreateVersion7();
        await RawSql.ExecuteAsync(
            "INSERT INTO public.participants (id, code_hash, label, status, created_at) VALUES (@id, @h, 'notitest-' || @id::text, 'Active', now())", // not an `itest-` label
            ("id", seedLike),
            ("h", "notitest-" + Guid.NewGuid().ToString("N")));
        try
        {
            await RawSql.ExecuteAsync(
                "INSERT INTO public.conversations (id, participant_id, started_at, ended_at) VALUES (@c, @p, @s, @e)",
                ("c", conversation),
                ("p", seedLike),
                ("s", now.AddDays(-200)),
                ("e", now.AddDays(-199)));

            await using var db = TestDatabase.NewContext();
            await Service(db, Options(dryRun: false)).RunAsync(Scope, dryRun: null, CancellationToken.None);

            Assert.True(await ConversationExistsAsync(conversation), "a row outside the test scope must not be deleted");
        }
        finally
        {
            await RawSql.ExecuteAsync("DELETE FROM public.conversations WHERE id = @c", ("c", conversation));
            await RawSql.ExecuteAsync("DELETE FROM public.participants WHERE id = @p AND label LIKE 'notitest-%'", ("p", seedLike));
            await RemoveAnonymousLeftoversAsync();
        }
    }
}
