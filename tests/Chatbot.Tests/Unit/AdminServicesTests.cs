using System.Text.Json;
using System.Text.RegularExpressions;
using Chatbot.Api.Startup;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Admin;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Admin;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Security;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Chatbot.Tests.Unit;

public sealed record AuditCall(ActorType Actor, Guid? ActorId, string Action, string? TargetType, Guid? TargetId);

public sealed class CapturingAudit : IAuditWriter
{
    public List<AuditCall> Calls { get; } = [];

    public Task WriteAsync(ActorType actorType, Guid? actorId, string action, string? targetType, Guid? targetId, string? clientIp, CancellationToken ct)
    {
        Calls.Add(new AuditCall(actorType, actorId, action, targetType, targetId));
        return Task.CompletedTask;
    }
}

/// <summary>The admin services on an EF in-memory database (the same interceptor as the application): no network, no shared database.</summary>
internal sealed class AdminRig : IDisposable
{
    public const string SecretMarker = "GIZLI-MESAJ-METNI-4711";
    public const string NicknameMarker = "GizliTakmaAd4711";

    public AppDbContext Db { get; } = TestEncryption.InMemoryContext();

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero));

    public CapturingAudit Audit { get; } = new();

    public AdminAuthOptions Options { get; } = new() { JwtSigningKey = new string('a', 40) };

    public ISecretHasher Hasher { get; } = new SecretHasher(
        new AuthOptions { CodePepper = new string('p', 40) },
        new PrivacyOptions { IpHashKey = new string('i', 40) });

    public AdminPasswordHasher Passwords { get; } = new(1000);

    public Guid AdminId { get; } = Guid.CreateVersion7();

    public ClientInfo Client { get; } = new("127.0.0.1");

    public AdminParticipantService Participants => new(Db, Hasher, Audit, Options, Clock);

    public AdminStatsService Stats => new(Db, Audit, Options, Clock);

    public AdminCrisisService Crisis => new(Db, Audit, Clock);

    public AdminAccountService Accounts => new(Db, Hasher, Audit, Passwords, Options, Clock);

    public async Task<AdminUser> AddAdminAsync(string username = "arastirmaci-1")
    {
        var admin = new AdminUser
        {
            Id = AdminId,
            Username = username,
            PasswordHash = Passwords.Hash("a long enough password"),
            TotpSecret = Base32.Encode(Totp.NewSecret()),
            TotpEnabled = true,
            Status = AdminStatus.Active,
            CreatedAt = Clock.GetUtcNow(),
        };
        Db.AdminUsers.Add(admin);
        await Db.SaveChangesAsync();
        return admin;
    }

    public async Task<Participant> AddParticipantAsync(string? label, ParticipantStatus status = ParticipantStatus.Active, DateTimeOffset? lastSeen = null)
    {
        var participant = new Participant
        {
            Id = Guid.CreateVersion7(),
            CodeHash = Hasher.HashCode(ParticipantCode.Generate()),
            Label = label,
            Status = status,
            Nickname = NicknameMarker,
            CreatedAt = Clock.GetUtcNow().AddDays(-10),
            LastSeenAt = lastSeen,
        };
        Db.Participants.Add(participant);
        await Db.SaveChangesAsync();
        return participant;
    }

    public async Task<Conversation> AddConversationAsync(Guid participantId, DateTimeOffset startedAt, DateTimeOffset? endedAt = null)
    {
        var conversation = new Conversation { Id = Guid.CreateVersion7(), ParticipantId = participantId, StartedAt = startedAt, EndedAt = endedAt };
        Db.Conversations.Add(conversation);
        await Db.SaveChangesAsync();
        return conversation;
    }

    public async Task<Message> AddMessageAsync(Guid conversationId, MessageRole role, DateTimeOffset at, bool crisis = false)
    {
        var message = new Message { Id = Guid.CreateVersion7(), ConversationId = conversationId, Role = role, Content = SecretMarker, CreatedAt = at, IsCrisis = crisis };
        Db.Messages.Add(message);
        await Db.SaveChangesAsync();
        return message;
    }

    public void Dispose() => Db.Dispose();
}

public class AdminParticipantServiceTests
{
    [Fact]
    public async Task ACreatedParticipant_GetsAnInvitedStatus_ARandomCode_AndOnlyItsHashIsStored()
    {
        using var rig = new AdminRig();
        var created = await rig.Participants.CreateAsync(rig.AdminId, new CreateParticipantsInput("K-001", null, null), rig.Client, default);

        var issued = Assert.Single(created);
        Assert.True(ParticipantCode.IsValid(issued.Code));
        var stored = await rig.Db.Participants.SingleAsync();
        Assert.Equal(ParticipantStatus.Invited, stored.Status);
        Assert.Equal("K-001", stored.Label);
        Assert.Equal(rig.Hasher.HashCode(issued.Code), stored.CodeHash);
        Assert.DoesNotContain(issued.Code, JsonSerializer.Serialize(await rig.Db.Participants.AsNoTracking().Select(p => new { p.CodeHash, p.Label }).ToListAsync()), StringComparison.Ordinal);
        var call = Assert.Single(rig.Audit.Calls);
        Assert.Equal((ActorType.Researcher, rig.AdminId, AuditActions.AdminParticipantCreated, issued.Id), (call.Actor, call.ActorId, call.Action, call.TargetId));
    }

    [Fact]
    public async Task BulkCreation_NumbersOnFromTheHighestExistingNumber_AndCodesAreAllDifferent()
    {
        using var rig = new AdminRig();
        var first = await rig.Participants.CreateAsync(rig.AdminId, new CreateParticipantsInput(null, "K-", 3), rig.Client, default);
        var second = await rig.Participants.CreateAsync(rig.AdminId, new CreateParticipantsInput(null, "K-", 2), rig.Client, default);

        Assert.Equal(["K-001", "K-002", "K-003"], first.Select(p => p.Label));
        Assert.Equal(["K-004", "K-005"], second.Select(p => p.Label));
        Assert.Equal(5, first.Concat(second).Select(p => p.Code).Distinct().Count());
        Assert.Equal(5, rig.Audit.Calls.Count(c => c.Action == AuditActions.AdminParticipantCreated));
    }

    [Theory]
    [InlineData(null, "K-", 0)]
    [InlineData(null, "K-", 101)]
    [InlineData(null, "itest-", 1)]
    [InlineData(null, "ITEST-", 1)]
    [InlineData(null, "seed-", 1)]
    [InlineData(null, "1K", 1)]
    [InlineData(null, "", 1)]
    [InlineData("itest-abc", null, null)]
    [InlineData("seed-9", null, null)]
    [InlineData("bad label", null, null)]
    [InlineData("-x", null, null)]
    public async Task InvalidOrReservedLabels_AndTooManyCodes_AreRefused(string? label, string? prefix, int? count)
    {
        using var rig = new AdminRig();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            rig.Participants.CreateAsync(rig.AdminId, new CreateParticipantsInput(label, prefix, count), rig.Client, default));
        Assert.Equal(ProblemCodes.ValidationFailed, ex.Code);
        Assert.Empty(rig.Db.Participants);
    }

    [Fact]
    public async Task ALabelThatExists_IsRefusedAndNothingIsCreated()
    {
        using var rig = new AdminRig();
        await rig.AddParticipantAsync("K-001");
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            rig.Participants.CreateAsync(rig.AdminId, new CreateParticipantsInput("K-001", null, null), rig.Client, default));
        Assert.Equal(ProblemCodes.LabelTaken, ex.Code);
        Assert.Equal(1, await rig.Db.Participants.CountAsync());
    }

    [Fact]
    public async Task ResettingTheCode_IssuesANewOne_InvalidatesTheOldHash_AndClosesTheSessions()
    {
        using var rig = new AdminRig();
        var participant = await rig.AddParticipantAsync("K-001");
        var oldHash = participant.CodeHash;
        rig.Db.RefreshTokens.Add(new RefreshToken { Id = Guid.NewGuid(), ParticipantId = participant.Id, TokenHash = "t", FamilyId = Guid.NewGuid(), CreatedAt = rig.Clock.GetUtcNow(), ExpiresAt = rig.Clock.GetUtcNow().AddDays(1), FamilyExpiresAt = rig.Clock.GetUtcNow().AddDays(1) });
        await rig.Db.SaveChangesAsync();

        var issued = await rig.Participants.ResetCodeAsync(rig.AdminId, participant.Id, rig.Client, default);

        var stored = await rig.Db.Participants.SingleAsync();
        Assert.NotEqual(oldHash, stored.CodeHash);
        Assert.Equal(rig.Hasher.HashCode(issued.Code), stored.CodeHash);
        Assert.NotNull((await rig.Db.RefreshTokens.SingleAsync()).RevokedAt);
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminParticipantCodeReset && c.TargetId == participant.Id);
    }

    [Theory]
    [InlineData(ParticipantStatus.Revoked)]
    [InlineData(ParticipantStatus.Deleted)]
    public async Task ACodeOfARevokedOrDeletedParticipant_CannotBeReset(ParticipantStatus status)
    {
        using var rig = new AdminRig();
        var participant = await rig.AddParticipantAsync("K-001", status);
        var ex = await Assert.ThrowsAsync<AppException>(() => rig.Participants.ResetCodeAsync(rig.AdminId, participant.Id, rig.Client, default));
        Assert.Equal(ProblemCodes.InvalidTransition, ex.Code);
    }

    [Fact]
    public async Task Revoking_ClosesSessions_AndReactivating_ReturnsToInvitedOrActive()
    {
        using var rig = new AdminRig();
        var neverSignedIn = await rig.AddParticipantAsync("K-001", ParticipantStatus.Invited);
        var signedIn = await rig.AddParticipantAsync("K-002", ParticipantStatus.Active, lastSeen: rig.Clock.GetUtcNow());

        await rig.Participants.RevokeAsync(rig.AdminId, neverSignedIn.Id, rig.Client, default);
        await rig.Participants.RevokeAsync(rig.AdminId, signedIn.Id, rig.Client, default);
        Assert.All(await rig.Db.Participants.ToListAsync(), p => Assert.Equal(ParticipantStatus.Revoked, p.Status));

        await rig.Participants.ReactivateAsync(rig.AdminId, neverSignedIn.Id, rig.Client, default);
        await rig.Participants.ReactivateAsync(rig.AdminId, signedIn.Id, rig.Client, default);
        Assert.Equal(ParticipantStatus.Invited, (await rig.Db.Participants.SingleAsync(p => p.Id == neverSignedIn.Id)).Status);
        Assert.Equal(ParticipantStatus.Active, (await rig.Db.Participants.SingleAsync(p => p.Id == signedIn.Id)).Status);
        Assert.Equal(2, rig.Audit.Calls.Count(c => c.Action == AuditActions.AdminParticipantRevoked));
        Assert.Equal(2, rig.Audit.Calls.Count(c => c.Action == AuditActions.AdminParticipantReactivated));
    }

    [Fact]
    public async Task InvalidTransitions_AreRefused_AnErasedParticipantNeverComesBack()
    {
        using var rig = new AdminRig();
        var active = await rig.AddParticipantAsync("K-001", ParticipantStatus.Active);
        var deleted = await rig.AddParticipantAsync("K-002", ParticipantStatus.Deleted);

        Assert.Equal(ProblemCodes.InvalidTransition, (await Assert.ThrowsAsync<AppException>(() => rig.Participants.ReactivateAsync(rig.AdminId, active.Id, rig.Client, default))).Code);
        Assert.Equal(ProblemCodes.InvalidTransition, (await Assert.ThrowsAsync<AppException>(() => rig.Participants.ReactivateAsync(rig.AdminId, deleted.Id, rig.Client, default))).Code);
        Assert.Equal(ProblemCodes.InvalidTransition, (await Assert.ThrowsAsync<AppException>(() => rig.Participants.RevokeAsync(rig.AdminId, deleted.Id, rig.Client, default))).Code);
        Assert.Equal(ProblemCodes.NotFound, (await Assert.ThrowsAsync<AppException>(() => rig.Participants.RevokeAsync(rig.AdminId, Guid.NewGuid(), rig.Client, default))).Code);
        Assert.Empty(rig.Audit.Calls);
    }

    [Fact]
    public async Task TheList_FiltersSearchesSortsAndPages_AndCarriesNoNicknameOrText()
    {
        using var rig = new AdminRig();
        foreach (var n in Enumerable.Range(1, 7))
        {
            await rig.AddParticipantAsync($"K-{n:000}", n % 2 == 0 ? ParticipantStatus.Active : ParticipantStatus.Invited);
        }

        await rig.AddParticipantAsync("Pilot-A", ParticipantStatus.Revoked);

        var page = await rig.Participants.ListAsync(new ParticipantListQuery(null, null, 1, 3, ParticipantSort.Label, false), default);
        Assert.Equal(8, page.Total);
        Assert.Equal(["K-001", "K-002", "K-003"], page.Items.Select(i => i.Label));
        var last = await rig.Participants.ListAsync(new ParticipantListQuery(null, null, 3, 3, ParticipantSort.Label, false), default);
        Assert.Equal(2, last.Items.Count);

        Assert.Equal(3, (await rig.Participants.ListAsync(new ParticipantListQuery(null, ParticipantStatus.Active, 1, 25, ParticipantSort.Label, false), default)).Total);
        Assert.Equal(["Pilot-A"], (await rig.Participants.ListAsync(new ParticipantListQuery("PILOT", null, 1, 25, ParticipantSort.Label, false), default)).Items.Select(i => i.Label));
        Assert.Equal("Pilot-A", (await rig.Participants.ListAsync(new ParticipantListQuery(null, null, 1, 1, ParticipantSort.Label, true), default)).Items.Single().Label);
        Assert.Equal(8, (await rig.Participants.ListAsync(new ParticipantListQuery(null, null, 1, 100, ParticipantSort.Messages, true), default)).Items.Count);

        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain(AdminRig.NicknameMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDetail_ShowsTheStatistics_WritesAnAuditRow_AndHasNoContent()
    {
        using var rig = new AdminRig();
        var participant = await rig.AddParticipantAsync("K-001");
        var start = rig.Clock.GetUtcNow().AddDays(-2);
        var done = await rig.AddConversationAsync(participant.Id, start, start.AddMinutes(30));
        await rig.AddMessageAsync(done.Id, MessageRole.User, start.AddMinutes(1));
        await rig.AddMessageAsync(done.Id, MessageRole.Assistant, start.AddMinutes(2));
        await rig.AddMessageAsync(done.Id, MessageRole.User, start.AddMinutes(5));
        await rig.AddMessageAsync(done.Id, MessageRole.User, start.AddMinutes(45)); // a long gap: not counted
        var forgotten = await rig.AddConversationAsync(participant.Id, rig.Clock.GetUtcNow().AddDays(-1).AddHours(-5)); // no end, no activity for > 24 h
        rig.Db.AuditLogs.AddRange(
            new AuditLog { Id = Guid.NewGuid(), ActorType = ActorType.Participant, ActorId = participant.Id, Action = AuditActions.AuthCodeSucceeded, Timestamp = start },
            new AuditLog { Id = Guid.NewGuid(), ActorType = ActorType.Participant, ActorId = participant.Id, Action = AuditActions.AuthCodeSucceeded, Timestamp = start.AddDays(1) },
            new AuditLog { Id = Guid.NewGuid(), ActorType = ActorType.Anonymous, ActorId = null, Action = AuditActions.AuthCodeFailed, Timestamp = start });
        await rig.Db.SaveChangesAsync();

        var detail = await rig.Participants.GetAsync(rig.AdminId, participant.Id, rig.Client, default);

        Assert.Equal(2, detail.Stats.LoginCount);
        Assert.Equal(start, detail.Stats.FirstLoginAt);
        Assert.Equal(start.AddDays(1), detail.Stats.LastLoginAt);
        Assert.Equal(2, detail.Stats.ConversationCount);
        Assert.Equal(1, detail.Stats.CompletedConversations);
        Assert.Equal(1, detail.Stats.AbandonedConversations);
        Assert.Equal(0, detail.Stats.OngoingConversations);
        Assert.Equal(3, detail.Stats.UserMessages);
        Assert.Equal(1, detail.Stats.AssistantMessages);
        Assert.Equal(4.0, detail.Stats.EstimatedActiveMinutes); // 1 + 3 minutes; the 40 minute gap is left out
        var conversation = detail.Conversations.Single(c => c.Id == done.Id);
        Assert.Equal(30.0, conversation.DurationMinutes);
        Assert.Equal(ConversationState.Completed, conversation.State);
        Assert.Equal(ConversationState.Abandoned, detail.Conversations.Single(c => c.Id == forgotten.Id).State);
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminParticipantViewed && c.TargetId == participant.Id);

        var json = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain(AdminRig.SecretMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminRig.NicknameMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownParticipant_IsNotFound_AndNothingIsAudited()
    {
        using var rig = new AdminRig();
        var ex = await Assert.ThrowsAsync<AppException>(() => rig.Participants.GetAsync(rig.AdminId, Guid.NewGuid(), rig.Client, default));
        Assert.Equal(ProblemCodes.NotFound, ex.Code);
        Assert.Empty(rig.Audit.Calls);
    }
}

public class AdminStatsServiceTests
{
    [Fact]
    public async Task TheSummary_CountsStatuses_NeverSignedIn_AndBuildsTheSeries()
    {
        using var rig = new AdminRig();
        var now = rig.Clock.GetUtcNow();
        var a = await rig.AddParticipantAsync("K-001", ParticipantStatus.Invited);
        var b = await rig.AddParticipantAsync("K-002", ParticipantStatus.Active, lastSeen: now);
        await rig.AddParticipantAsync("K-003", ParticipantStatus.Revoked);
        var done = await rig.AddConversationAsync(b.Id, now.AddHours(-3), now.AddHours(-2));
        await rig.AddMessageAsync(done.Id, MessageRole.User, now.AddHours(-3).AddMinutes(1));
        await rig.AddMessageAsync(done.Id, MessageRole.Assistant, now.AddHours(-3).AddMinutes(2));

        var summary = await rig.Stats.SummaryAsync(default);

        Assert.Equal((1, 1, 1), (summary.Invited, summary.Active, summary.Revoked));
        Assert.Equal(1, summary.NeverSignedIn); // K-001; the revoked one is not counted
        Assert.Equal(1, summary.Conversations);
        Assert.Equal(1, summary.CompletedConversations);
        Assert.Equal(60.0, summary.AverageCompletedMinutes);
        Assert.Equal(14, summary.Daily.Count);
        Assert.Equal(8, summary.Weekly.Count);
        var today = summary.Daily[^1];
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero), today.PeriodStart);
        Assert.Equal((1, 1, 1), (today.ConversationsStarted, today.UserMessages, today.ActiveParticipants)); // only USER messages count
        Assert.Equal(a.Id == b.Id ? 0 : 1, summary.Weekly[^1].ActiveParticipants);
        Assert.DoesNotContain(AdminRig.SecretMarker, JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummary_OfAnEmptySystem_IsZeroesAndNoAverage()
    {
        using var rig = new AdminRig();
        var summary = await rig.Stats.SummaryAsync(default);
        Assert.Equal((0, 0, 0, 0, 0), (summary.Invited, summary.Active, summary.Revoked, summary.NeverSignedIn, summary.Conversations));
        Assert.Null(summary.AverageCompletedMinutes);
    }

    [Fact]
    public async Task TheCsv_HasOneRowPerParticipant_EscapesFormulas_IsAudited_AndHasNoContent()
    {
        using var rig = new AdminRig();
        var p = await rig.AddParticipantAsync("=HYPERLINK(1)");
        await rig.AddParticipantAsync("K-002");
        var c = await rig.AddConversationAsync(p.Id, rig.Clock.GetUtcNow().AddHours(-1));
        await rig.AddMessageAsync(c.Id, MessageRole.User, rig.Clock.GetUtcNow().AddMinutes(-50));

        var csv = await rig.Stats.ExportCsvAsync(rig.AdminId, rig.Client, default);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(AdminStatsMath.CsvHeader, lines[0]);
        Assert.Contains(lines, l => l.StartsWith("'=HYPERLINK(1),", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("K-002,", StringComparison.Ordinal));
        Assert.DoesNotContain(AdminRig.SecretMarker, csv, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminRig.NicknameMarker, csv, StringComparison.Ordinal);
        Assert.Contains(rig.Audit.Calls, x => x.Action == AuditActions.AdminStatsExported && x.ActorId == rig.AdminId);
    }
}

public class AdminCrisisServiceTests
{
    private static async Task<(AdminRig Rig, Conversation A, Conversation B, Message A1, Message A2, Message B1)> SeedAsync()
    {
        var rig = new AdminRig();
        await rig.AddAdminAsync();
        var p1 = await rig.AddParticipantAsync("K-001");
        var p2 = await rig.AddParticipantAsync("K-002");
        var t = rig.Clock.GetUtcNow();
        var a = await rig.AddConversationAsync(p1.Id, t.AddHours(-5));
        var b = await rig.AddConversationAsync(p2.Id, t.AddHours(-3));
        var a1 = await rig.AddMessageAsync(a.Id, MessageRole.User, t.AddHours(-4), crisis: true);
        await rig.AddMessageAsync(a.Id, MessageRole.Assistant, t.AddHours(-4).AddSeconds(2), crisis: true); // the safe reply is flagged too: it is not counted
        var a2 = await rig.AddMessageAsync(a.Id, MessageRole.User, t.AddHours(-2), crisis: true);
        await rig.AddMessageAsync(a.Id, MessageRole.User, t.AddHours(-1)); // not flagged
        var b1 = await rig.AddMessageAsync(b.Id, MessageRole.User, t.AddHours(-1), crisis: true);
        return (rig, a, b, a1, a2, b1);
    }

    [Fact]
    public async Task TheList_GroupsByConversation_CountsUserFlagsOnly_AndPutsOpenOnesFirst()
    {
        var (rig, a, b, _, _, _) = await SeedAsync();
        using var _ = rig;

        var overview = await rig.Crisis.ListAsync(rig.AdminId, openOnly: false, rig.Client, default);

        Assert.Equal((2, 3), (overview.OpenConversations, overview.OpenMessages));
        Assert.Equal(2, overview.Items.Count);
        var first = overview.Items[0];
        Assert.Equal(b.Id, first.ConversationId); // its flag is the more recent one
        Assert.Equal("K-002", first.ParticipantLabel);
        var second = overview.Items[1];
        Assert.Equal((a.Id, "K-001", 2, 2), (second.ConversationId, second.ParticipantLabel, second.FlaggedCount, second.OpenCount));
        Assert.Null(second.AcknowledgedAt);
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminCrisisViewed);
        var json = JsonSerializer.Serialize(overview);
        Assert.DoesNotContain(AdminRig.SecretMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledging_ClosesEveryOpenFlagOfTheConversation_WithOneAuditEntryPerMessage()
    {
        var (rig, a, b, a1, a2, _) = await SeedAsync();
        using var _ = rig;

        var closed = await rig.Crisis.AcknowledgeAsync(rig.AdminId, a.Id, rig.Client, default);

        Assert.Equal(2, closed);
        var audited = rig.Audit.Calls.Where(c => c.Action == AuditActions.AdminCrisisAcknowledged).ToList();
        Assert.Equal([a1.Id, a2.Id], audited.Select(c => c.TargetId!.Value).Order());
        Assert.All(audited, c => Assert.Equal((ActorType.Researcher, rig.AdminId, nameof(Message)), (c.Actor, c.ActorId, c.TargetType)));

        var overview = await rig.Crisis.ListAsync(rig.AdminId, openOnly: false, rig.Client, default);
        Assert.Equal((1, 1), (overview.OpenConversations, overview.OpenMessages));
        var acked = overview.Items.Single(i => i.ConversationId == a.Id);
        Assert.Equal(0, acked.OpenCount);
        Assert.Equal("arastirmaci-1", acked.AcknowledgedBy);
        Assert.NotNull(acked.AcknowledgedAt);
        Assert.Equal(b.Id, overview.Items[0].ConversationId); // open ones stay first
        Assert.Single((await rig.Crisis.ListAsync(rig.AdminId, openOnly: true, rig.Client, default)).Items);

        Assert.Equal(0, await rig.Crisis.AcknowledgeAsync(rig.AdminId, a.Id, rig.Client, default)); // nothing left to close, nothing more audited
        Assert.Equal(2, rig.Audit.Calls.Count(c => c.Action == AuditActions.AdminCrisisAcknowledged));
    }

    [Fact]
    public async Task Reopening_TakesTheAcknowledgementBack_AndANewOneCanFollow()
    {
        var (rig, a, _, _, _, _) = await SeedAsync();
        using var _ = rig;
        await rig.Crisis.AcknowledgeAsync(rig.AdminId, a.Id, rig.Client, default);

        Assert.Equal(2, await rig.Crisis.ReopenAsync(rig.AdminId, a.Id, rig.Client, default));
        Assert.Equal(2, rig.Audit.Calls.Count(c => c.Action == AuditActions.AdminCrisisReopened));
        Assert.Equal(3, (await rig.Crisis.ListAsync(rig.AdminId, openOnly: false, rig.Client, default)).OpenMessages);
        Assert.All(await rig.Db.CrisisAcknowledgements.ToListAsync(), r => Assert.NotNull(r.ReopenedAt));

        Assert.Equal(2, await rig.Crisis.AcknowledgeAsync(rig.AdminId, a.Id, rig.Client, default)); // again
        Assert.Equal(4, await rig.Db.CrisisAcknowledgements.CountAsync());
        Assert.Equal(0, await rig.Crisis.ReopenAsync(rig.AdminId, a.Id, rig.Client, default) - 2); // the second batch is reopened as well
    }

    [Fact]
    public async Task AConversationWithoutFlags_IsNotFound()
    {
        var (rig, _, _, _, _, _) = await SeedAsync();
        using var _ = rig;
        var clean = await rig.AddConversationAsync((await rig.AddParticipantAsync("K-009")).Id, rig.Clock.GetUtcNow());
        Assert.Equal(ProblemCodes.NotFound, (await Assert.ThrowsAsync<AppException>(() => rig.Crisis.AcknowledgeAsync(rig.AdminId, clean.Id, rig.Client, default))).Code);
        Assert.Equal(ProblemCodes.NotFound, (await Assert.ThrowsAsync<AppException>(() => rig.Crisis.ReopenAsync(rig.AdminId, Guid.NewGuid(), rig.Client, default))).Code);
    }
}

public class AdminAccountServiceTests
{
    [Theory]
    [InlineData("ab")]
    [InlineData("1abc")]
    [InlineData("Has Space")]
    [InlineData("")]
    public async Task ABadUserName_IsRefused(string name)
    {
        using var rig = new AdminRig();
        Assert.Equal(AdminAccountProblem.BadUsername, (await rig.Accounts.PrepareAsync(name, "a long enough password", default)).Problem);
    }

    [Fact]
    public async Task ADuplicateName_AndAWeakPassword_AreRefused()
    {
        using var rig = new AdminRig();
        await rig.AddAdminAsync("arastirmaci-1");
        Assert.Equal(AdminAccountProblem.UsernameTaken, (await rig.Accounts.PrepareAsync("Arastirmaci-1", "a long enough password", default)).Problem);
        Assert.Equal(AdminAccountProblem.WeakPassword, (await rig.Accounts.PrepareAsync("yeni-ad", "short", default)).Problem);
        Assert.Equal(AdminAccountProblem.WeakPassword, (await rig.Accounts.PrepareAsync("yeni-ad-uzun-12345", "yeni-ad-uzun-12345", default)).Problem);
    }

    [Fact]
    public async Task NothingIsStored_UntilTheFirstTotpCodeIsConfirmed()
    {
        using var rig = new AdminRig();
        var (problem, pending) = await rig.Accounts.PrepareAsync("yeni-yonetici", "a long enough password", default);
        Assert.Equal(AdminAccountProblem.None, problem);
        Assert.Empty(rig.Db.AdminUsers);
        Assert.Equal(RecoveryCodes.Count, pending!.RecoveryCodes.Count);
        Assert.StartsWith("otpauth://totp/Lumind:yeni-yonetici?secret=", pending.OtpauthUri, StringComparison.Ordinal);

        Assert.Equal(AdminAccountProblem.WrongCode, await rig.Accounts.ConfirmAndStoreAsync(pending, "000000", default));
        Assert.Empty(rig.Db.AdminUsers);

        var code = Totp.Compute(Base32.Decode(pending.SecretBase32)!, Totp.StepOf(rig.Clock.GetUtcNow()));
        Assert.Equal(AdminAccountProblem.None, await rig.Accounts.ConfirmAndStoreAsync(pending, code, default));

        var admin = await rig.Db.AdminUsers.SingleAsync();
        Assert.True(admin.TotpEnabled);
        Assert.Equal(AdminStatus.Active, admin.Status);
        Assert.True(rig.Passwords.Verify("a long enough password", admin.PasswordHash));
        Assert.Equal(Totp.StepOf(rig.Clock.GetUtcNow()), admin.TotpLastStep); // the confirming code cannot sign in
        Assert.Equal(RecoveryCodes.Count, await rig.Db.AdminRecoveryCodes.CountAsync());
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminAccountCreated && c.Actor == ActorType.System);
    }

    [Fact]
    public async Task TheRecoveryCodes_AreStoredOnlyAsKeyedHashes()
    {
        using var rig = new AdminRig();
        var (_, pending) = await rig.Accounts.PrepareAsync("yeni-yonetici", "a long enough password", default);
        var code = Totp.Compute(Base32.Decode(pending!.SecretBase32)!, Totp.StepOf(rig.Clock.GetUtcNow()));
        await rig.Accounts.ConfirmAndStoreAsync(pending, code, default);

        var hashes = (await rig.Db.AdminRecoveryCodes.ToListAsync()).Select(r => r.CodeHash).ToList();
        foreach (var display in pending.RecoveryCodes)
        {
            Assert.Contains(rig.Hasher.HashCode(RecoveryCodes.Normalize(display)!), hashes);
            Assert.DoesNotContain(hashes, h => h.Contains(RecoveryCodes.Normalize(display)!, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheTotpSecret_IsEncryptedAtRest_AndReadsBackAsThePlainSecret()
    {
        const string Name = "totp-at-rest";
        var interceptor = TestEncryption.Shared;
        await using (var db = TestEncryption.InMemoryContext(Name, interceptor))
        {
            db.AdminUsers.Add(new AdminUser { Id = Guid.CreateVersion7(), Username = "x-admin", PasswordHash = "h", TotpSecret = "JBSWY3DPEHPK3PXP", Status = AdminStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var raw = TestEncryption.RawInMemoryContext(Name))
        {
            var stored = (await raw.AdminUsers.SingleAsync()).TotpSecret!;
            Assert.StartsWith("enc:v1:", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("JBSWY3DPEHPK3PXP", stored, StringComparison.Ordinal);
        }

        await using var read = TestEncryption.InMemoryContext(Name, interceptor);
        Assert.Equal("JBSWY3DPEHPK3PXP", (await read.AdminUsers.SingleAsync()).TotpSecret);
    }

    [Fact]
    public async Task ChangingThePassword_ClosesEverySession_ClearsTheLock_AndIsAudited()
    {
        using var rig = new AdminRig();
        var admin = await rig.AddAdminAsync();
        admin.FailedAttempts = 7;
        admin.LockedUntil = rig.Clock.GetUtcNow().AddMinutes(5);
        rig.Db.AdminRefreshTokens.Add(new AdminRefreshToken { Id = Guid.NewGuid(), AdminId = admin.Id, TokenHash = "t", FamilyId = Guid.NewGuid(), CreatedAt = rig.Clock.GetUtcNow(), ExpiresAt = rig.Clock.GetUtcNow().AddMinutes(30), FamilyExpiresAt = rig.Clock.GetUtcNow().AddHours(8) });
        await rig.Db.SaveChangesAsync();

        Assert.Equal(AdminAccountProblem.WeakPassword, await rig.Accounts.SetPasswordAsync("arastirmaci-1", "short", default));
        Assert.Equal(AdminAccountProblem.NotFound, await rig.Accounts.SetPasswordAsync("yok-boyle-biri", "a long enough password", default));
        Assert.Equal(AdminAccountProblem.None, await rig.Accounts.SetPasswordAsync("ARASTIRMACI-1", "another long password!", default));

        var stored = await rig.Db.AdminUsers.SingleAsync();
        Assert.True(rig.Passwords.Verify("another long password!", stored.PasswordHash));
        Assert.Equal((0, null), (stored.FailedAttempts, stored.LockedUntil));
        Assert.NotNull((await rig.Db.AdminRefreshTokens.SingleAsync()).RevokedAt);
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminPasswordChanged);
    }

    [Fact]
    public async Task DisablingAnAccount_ClosesItsSessions_AndEnablingClearsTheLock()
    {
        using var rig = new AdminRig();
        var admin = await rig.AddAdminAsync();
        rig.Db.AdminRefreshTokens.Add(new AdminRefreshToken { Id = Guid.NewGuid(), AdminId = admin.Id, TokenHash = "t", FamilyId = Guid.NewGuid(), CreatedAt = rig.Clock.GetUtcNow(), ExpiresAt = rig.Clock.GetUtcNow().AddMinutes(30), FamilyExpiresAt = rig.Clock.GetUtcNow().AddHours(8) });
        await rig.Db.SaveChangesAsync();

        Assert.Equal(AdminAccountProblem.None, await rig.Accounts.SetStatusAsync("arastirmaci-1", AdminStatus.Disabled, default));
        Assert.Equal(AdminStatus.Disabled, (await rig.Db.AdminUsers.SingleAsync()).Status);
        Assert.NotNull((await rig.Db.AdminRefreshTokens.SingleAsync()).RevokedAt);
        Assert.Equal(0, await rig.Accounts.CountActiveAsync(default));

        admin.FailedAttempts = 9;
        await rig.Db.SaveChangesAsync();
        Assert.Equal(AdminAccountProblem.None, await rig.Accounts.SetStatusAsync("arastirmaci-1", AdminStatus.Active, default));
        Assert.Equal((AdminStatus.Active, 0), ((await rig.Db.AdminUsers.SingleAsync()).Status, (await rig.Db.AdminUsers.SingleAsync()).FailedAttempts));
        Assert.Equal(1, await rig.Accounts.CountActiveAsync(default));
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminAccountDisabled);
        Assert.Contains(rig.Audit.Calls, c => c.Action == AuditActions.AdminAccountEnabled);
    }
}

public class AdminCommandTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>A scripted terminal: answers prompts in order, records everything that was written, and can compute the first TOTP code.</summary>
    private sealed class ScriptedConsole(params string[] answers) : IAdminConsole
    {
        private readonly Queue<string> _answers = new(answers);

        public List<string> Output { get; } = [];

        public List<string> Prompts { get; } = [];

        public Func<string, string?>? Override { get; set; }

        public void WriteLine(string text = "") => Output.Add(text);

        public string? ReadLine(string prompt)
        {
            Prompts.Add(prompt);
            return Override?.Invoke(prompt) ?? (_answers.Count > 0 ? _answers.Dequeue() : null);
        }

        public string? ReadSecret(string prompt)
        {
            Prompts.Add(prompt);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public string All => string.Join("\n", Output) + "\n" + string.Join("\n", Prompts);
    }

    private const string Password = "kesinlikle-gizli-parola-1";
    private const string ConnectionString = "Host=veritabani.example.test;Port=5432;Database=railway;Username=kullanici;Password=BAGLANTI-PAROLASI-9;SSL Mode=Require";

    private static (ServiceProvider Services, AppDbContext Db) Services()
    {
        var db = TestEncryption.InMemoryContext();
        var auth = new AuthOptions { JwtSigningKey = new string('j', 40), CodePepper = new string('p', 40) };
        var privacy = TestEncryption.Privacy();
        privacy.IpHashKey = new string('i', 40);
        var services = new ServiceCollection();
        services.AddSingleton(auth);
        services.AddSingleton(privacy);
        services.AddSingleton(new DocumentsOptions());
        services.AddSingleton(new ChatOptions());
        services.AddSingleton(new AdminAuthOptions { JwtSigningKey = new string('a', 40) });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString }).Build());
        services.AddSingleton(db);
        services.AddSingleton<ISecretHasher>(new SecretHasher(auth, privacy));
        services.AddSingleton<IAuditWriter>(new CapturingAudit());
        services.AddSingleton(new AdminPasswordHasher(1000));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AdminAccountService>();
        return (services.BuildServiceProvider(), db);
    }

    private static Task<int> Run(ServiceProvider services, IHostEnvironment env, IAdminConsole console, params string[] args) =>
        AdminCommands.RunAsync(env, services, args, console);

    [Fact]
    public async Task OutsideDevelopment_TheCommandsRefuseToRun()
    {
        var (services, db) = Services();
        using var _ = services;
        var console = new ScriptedConsole();
        Assert.Equal(2, await Run(services, new FakeEnvironment("Production"), console, "create-admin", "--username", "x-admin"));
        Assert.Empty(db.AdminUsers);
        Assert.Empty(console.Prompts);
    }

    [Fact]
    public async Task TheServerNameIsShown_NeverTheCredentials_AndAnythingButEvetStopsEverything()
    {
        var (services, db) = Services();
        using var _ = services;
        foreach (var answer in new[] { "hayır", "Evet", "yes", "", "evet." })
        {
            var console = new ScriptedConsole(answer);
            Assert.Equal(3, await Run(services, new FakeEnvironment("Development"), console, "create-admin", "--username", "yeni-yonetici"));
            Assert.Contains(console.Output, l => l.Contains("veritabani.example.test", StringComparison.Ordinal));
            Assert.DoesNotContain("BAGLANTI-PAROLASI-9", console.All, StringComparison.Ordinal);
            Assert.DoesNotContain("kullanici", console.All, StringComparison.Ordinal);
            Assert.DoesNotContain("railway", console.All, StringComparison.Ordinal);
            Assert.DoesNotContain(console.Prompts, p => p.Contains("Parola", StringComparison.Ordinal)); // nothing secret is even asked before the "evet"
        }

        Assert.Empty(db.AdminUsers);
    }

    [Fact]
    public async Task CreateAdmin_FullFlow_PasswordIsTypedHidden_SecretsGoToTheTerminalOnly_AndTheAccountWorks()
    {
        var (services, db) = Services();
        using var _ = services;
        var console = new ScriptedConsole("evet", Password, Password, "unused");
        console.Override = prompt =>
        {
            if (!prompt.StartsWith("Uygulamanın", StringComparison.Ordinal))
            {
                return null;
            }

            var secret = Regex.Match(string.Join("\n", console.Output), @"gizli anahtar\s*:\s*([A-Z2-7]+)").Groups[1].Value;
            return Totp.Compute(Base32.Decode(secret)!, Totp.StepOf(DateTimeOffset.UtcNow));
        };

        var exit = await Run(services, new FakeEnvironment("Development"), console, "create-admin", "--username", "Yeni-Yonetici");

        Assert.Equal(0, exit);
        var admin = await db.AdminUsers.SingleAsync();
        Assert.Equal("yeni-yonetici", admin.Username);
        Assert.True(admin.TotpEnabled);
        Assert.DoesNotContain(Password, console.All, StringComparison.Ordinal); // the password is never echoed or printed
        Assert.True(new AdminPasswordHasher(1000).Verify(Password, admin.PasswordHash));
        Assert.Equal(RecoveryCodes.Count, console.Output.Count(l => Regex.IsMatch(l, @"^\s+[A-HJKMNP-Z2-9]{5}-[A-HJKMNP-Z2-9]{5}$")));
        Assert.Contains(console.Output, l => l.Contains("otpauth://totp/Lumind:yeni-yonetici", StringComparison.Ordinal));
        Assert.Contains(console.Output, l => l.Contains("İKİ yönetici", StringComparison.Ordinal));
        Assert.DoesNotContain("BAGLANTI-PAROLASI-9", console.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAdmin_WithAWrongFirstCode_OrDifferentPasswords_CreatesNothing()
    {
        var (services, db) = Services();
        using var _ = services;

        var wrongCode = new ScriptedConsole("evet", Password, Password, "000000");
        Assert.Equal(1, await Run(services, new FakeEnvironment("Development"), wrongCode, "create-admin", "--username", "yeni-yonetici"));

        var different = new ScriptedConsole("evet", Password, Password + "x");
        Assert.Equal(1, await Run(services, new FakeEnvironment("Development"), different, "create-admin", "--username", "yeni-yonetici"));

        var weak = new ScriptedConsole("evet", "kisa", "kisa");
        Assert.Equal(1, await Run(services, new FakeEnvironment("Development"), weak, "create-admin", "--username", "yeni-yonetici"));

        Assert.Empty(db.AdminUsers);
    }

    [Fact]
    public async Task SetPasswordDisableAndEnable_AskTheSameConfirmation_AndWork()
    {
        var (services, db) = Services();
        using var _ = services;
        db.AdminUsers.Add(new AdminUser { Id = Guid.NewGuid(), Username = "var-olan", PasswordHash = "old", TotpEnabled = true, Status = AdminStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var env = new FakeEnvironment("Development");

        var refused = new ScriptedConsole("hayır");
        Assert.Equal(3, await Run(services, env, refused, "disable-admin", "--username", "var-olan"));
        Assert.Equal(AdminStatus.Active, (await db.AdminUsers.SingleAsync()).Status);

        Assert.Equal(0, await Run(services, env, new ScriptedConsole("evet"), "disable-admin", "--username", "var-olan"));
        Assert.Equal(AdminStatus.Disabled, (await db.AdminUsers.SingleAsync()).Status);
        Assert.Equal(0, await Run(services, env, new ScriptedConsole("evet"), "enable-admin", "--username", "var-olan"));
        Assert.Equal(AdminStatus.Active, (await db.AdminUsers.SingleAsync()).Status);

        var changed = new ScriptedConsole("evet", "yepyeni-uzun-parola-2", "yepyeni-uzun-parola-2");
        Assert.Equal(0, await Run(services, env, changed, "set-admin-password", "--username", "var-olan"));
        Assert.True(new AdminPasswordHasher(1000).Verify("yepyeni-uzun-parola-2", (await db.AdminUsers.SingleAsync()).PasswordHash));
        Assert.DoesNotContain("yepyeni-uzun-parola-2", changed.All, StringComparison.Ordinal);

        Assert.Equal(1, await Run(services, env, new ScriptedConsole("evet"), "enable-admin", "--username", "yok-boyle-biri"));
    }

    [Theory]
    [InlineData("Host=db.example.test;Port=5432;Database=x;Username=u;Password=p", "db.example.test")]
    [InlineData("Host=10.0.0.5", "10.0.0.5")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("this is not a connection string", null)]
    public void OnlyTheServerNameIsReadFromTheConnectionString(string? connectionString, string? expected) =>
        Assert.Equal(expected, AdminCommands.HostOf(connectionString));
}
