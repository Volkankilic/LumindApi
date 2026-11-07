using Chatbot.Domain;
using Chatbot.Tests.TestSupport;

namespace Chatbot.Tests.Integration;

/// <summary>
/// The real rule based detector on the real pipeline and the real (shared) database: the flag, the stream order and the
/// content-free audit entry. All participants are `itest-` ones that the host deletes.
/// </summary>
public class CrisisIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    private static async Task<List<object?[]>> AuditRowsAsync(Guid participantId) =>
        await RawSql.RowsAsync(
            "SELECT action, actor_type, target_type, target_id, to_jsonb(a)::text FROM public.audit_logs a WHERE actor_id = @p AND action = @action",
            ("p", participantId), ("action", AuditActions.CrisisDetected));

    [PostgresFact]
    public async Task ACrisisMessage_StreamsCrisisFirst_FlagsTheMessage_AndLeavesOneContentFreeAuditEntry()
    {
        using var factory = host.NewFactoryWithTheRealCrisisDetector();
        var participant = await host.CreateParticipantAsync();
        var session = await factory.SignInAsync(participant);
        await session.OnboardAsync("KrizAd");
        var conversation = await session.StartConversationAsync();
        var clientId = Guid.NewGuid();

        var (response, events) = await session.SendAsync(conversation, "Artık yaşamak istemiyorum, ölmek istiyorum.", clientId);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["accepted", "crisis"], events.Take(2).Select(e => e.Name));
        Assert.Equal("done", events[^1].Name);
        Assert.All(events.Skip(2).SkipLast(1), e => Assert.Equal("delta", e.Name));

        var flags = await RawSql.RowsAsync(
            "SELECT role, is_crisis FROM public.messages WHERE conversation_id = @c ORDER BY created_at, id", ("c", conversation));
        Assert.Equal(2, flags.Count);
        Assert.All(flags, f => Assert.True((bool)f[1]!));

        var audit = Assert.Single(await AuditRowsAsync(participant.Id));
        Assert.Equal("Participant", audit[1]!.ToString());
        Assert.Equal("Conversation", audit[2]);
        Assert.Equal(conversation, (Guid)audit[3]!);

        // Content-free: the whole row, as JSON, carries neither the text, nor a phrase, nor a category.
        var json = (string)audit[4]!;
        foreach (var forbidden in new[] { "yaşamak", "yasamak", "ölmek", "olmek", "istiyorum", "DeathWish", "Category", "Intent", "Hopelessness" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        // The same client message id again (a retry): the crisis is streamed again, but there is no second entry.
        var (_, replay) = await session.SendAsync(conversation, "Artık yaşamak istemiyorum, ölmek istiyorum.", clientId);
        Assert.Equal(["accepted", "crisis"], replay.Take(2).Select(e => e.Name));
        Assert.Single(await AuditRowsAsync(participant.Id));
    }

    [PostgresFact]
    public async Task AnOrdinaryMessage_AndADeathFear_HaveNoCrisisEvent_NoFlag_AndNoAuditEntry()
    {
        using var factory = host.NewFactoryWithTheRealCrisisDetector();
        var participant = await host.CreateParticipantAsync();
        var session = await factory.SignInAsync(participant);
        await session.OnboardAsync("SakinAd");
        var conversation = await session.StartConversationAsync();

        foreach (var text in new[] { "Bugün biraz yorgunum, doktor randevum yarın.", "Ölmekten korkuyorum, geceleri uyuyamıyorum." })
        {
            var (_, events) = await session.SendAsync(conversation, text);
            Assert.DoesNotContain(events, e => e.Name == "crisis");
            Assert.Equal("done", events[^1].Name);
        }

        var flags = await RawSql.RowsAsync(
            "SELECT count(*) FROM public.messages WHERE conversation_id = @c AND is_crisis", ("c", conversation));
        Assert.Equal(0L, flags.Single()[0]);
        Assert.Empty(await AuditRowsAsync(participant.Id));
    }
}
