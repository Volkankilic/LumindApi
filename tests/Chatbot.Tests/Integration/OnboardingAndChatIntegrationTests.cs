using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chatbot.Application.Chat;
using Chatbot.Domain;
using Chatbot.Infrastructure.Chat;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Chatbot.Tests.Integration;

/// <summary>Consent, nickname, erasure, conversations and the streaming chat, against the real database.</summary>
public class OnboardingAndChatIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    private async Task<Session> NewOnboardedSessionAsync(string nickname = "Deneme")
    {
        var session = await host.Factory.SignInAsync(await host.CreateParticipantAsync());
        await session.OnboardAsync(nickname);
        return session;
    }

    [PostgresFact]
    public async Task TheOnboardingFlow_GatesTheChat_UntilNoticeAndConsentAreRecorded()
    {
        var session = await host.Factory.SignInAsync(await host.CreateParticipantAsync());

        Assert.True(session.State.GetProperty("noticeAcknowledgedVersion").ValueKind == JsonValueKind.Null);
        Assert.True(session.State.GetProperty("consent").ValueKind == JsonValueKind.Null);

        var blocked = await session.Client.PostAsync("/api/conversations", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("consent-required", (await blocked.ReadJsonAsync()).GetProperty("code").GetString());

        // A nickname needs a valid consent first.
        var early = await session.Client.PutAsync("/api/me/nickname", IntegrationClient.Json("""{"nickname":"Deneme"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);

        var state = await session.OnboardAsync("Deneme");
        Assert.Equal("Deneme", state.GetProperty("nickname").GetString());
        Assert.Equal(IntegrationHost.Document, state.GetProperty("noticeAcknowledgedVersion").GetString());
        Assert.True(state.GetProperty("consent").GetProperty("processing").GetBoolean());
        Assert.False(state.GetProperty("consent").GetProperty("researchUse").GetBoolean());

        Assert.Equal(HttpStatusCode.Created, (await session.Client.PostAsync("/api/conversations", content: null)).StatusCode);
    }

    [PostgresFact]
    public async Task AStaleDocumentVersion_Is409_AndNothingIsRecorded()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);

        var response = await session.Client.PostAsync("/api/consents", IntegrationClient.Json(
            """{"records":[{"type":"privacyNotice","version":"0.0-eski","granted":true}]}"""));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("stale-document-version", (await response.ReadJsonAsync()).GetProperty("code").GetString());

        await using var db = TestDatabase.NewContext();
        Assert.False(await db.ConsentRecords.AnyAsync(r => r.ParticipantId == participant.Id));
    }

    [PostgresFact]
    public async Task ResearchUse_NeedsAnExplicitConsent_AndTheLogIsAppendOnly()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);

        var without = await session.Client.PostAsync("/api/consents", IntegrationClient.Json(
            $$"""{"records":[{"type":"researchUse","version":"{{IntegrationHost.Document}}","granted":true}]}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, without.StatusCode);

        await session.OnboardAsync();
        var research = await session.Client.PostAsync("/api/consents", IntegrationClient.Json(
            $$"""{"records":[{"type":"researchUse","version":"{{IntegrationHost.Document}}","granted":true}]}"""));
        Assert.Equal(HttpStatusCode.OK, research.StatusCode);
        Assert.True((await research.ReadJsonAsync()).GetProperty("consent").GetProperty("researchUse").GetBoolean());

        await using var db = TestDatabase.NewContext();
        var records = await db.ConsentRecords.AsNoTracking().Where(r => r.ParticipantId == participant.Id).ToListAsync();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(IntegrationHost.Document, r.DocumentVersion));
    }

    [PostgresFact]
    public async Task WithdrawingTheConsent_DeletesTheContent_ClosesTheChat_AndKeepsTheConsentLog()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);
        await session.OnboardAsync();
        var conversation = await session.StartConversationAsync();
        await session.SendAsync(conversation, "Merhaba");

        var response = await session.Client.PostAsync("/api/consents", IntegrationClient.Json(
            $$"""{"records":[{"type":"explicitConsent","version":"{{IntegrationHost.Document}}","granted":false}]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.ReadJsonAsync();
        Assert.Equal(JsonValueKind.Null, state.GetProperty("consent").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("nickname").ValueKind);

        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.GetAsync($"/api/conversations/{conversation}")).StatusCode);

        await using var db = TestDatabase.NewContext();
        Assert.False(await db.Conversations.AnyAsync(c => c.ParticipantId == participant.Id));
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == conversation), "messages go with the conversation");
        Assert.Equal(3, await db.ConsentRecords.CountAsync(r => r.ParticipantId == participant.Id));
    }

    [PostgresFact]
    public async Task DeleteMe_ErasesTheContent_KeepsAnAnonymousTrace_AndTheCodeNeverWorksAgain()
    {
        var participant = await host.CreateParticipantAsync();
        var session = await host.Factory.SignInAsync(participant);
        await session.OnboardAsync();
        var conversation = await session.StartConversationAsync();
        await session.SendAsync(conversation, "Merhaba");

        var response = await session.Client.DeleteAsync("/api/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The old access token stops working at once (the policy looks at the database on every request).
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.RefreshAsync(session.RefreshCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.PostCodeAsync(participant.Code)).Response.StatusCode);

        await using var db = TestDatabase.NewContext();
        var row = await db.Participants.AsNoTracking().SingleAsync(p => p.Id == participant.Id);
        Assert.Equal(ParticipantStatus.Deleted, row.Status);
        Assert.Null(row.Nickname);
        Assert.False(await db.Conversations.AnyAsync(c => c.ParticipantId == participant.Id));
        Assert.True(await db.ConsentRecords.AnyAsync(r => r.ParticipantId == participant.Id));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.ActorId == participant.Id && a.Action == "participant.deleted"));
    }

    [PostgresFact]
    public async Task TheFullChatFlow_StreamsInOrder_StoresBothMessages_AndTheHistoryMatches()
    {
        var session = await NewOnboardedSessionAsync();
        var conversation = await session.StartConversationAsync();

        var (response, events) = await session.SendAsync(conversation, "Bugün çok yorgunum");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var names = events.Select(e => e.Name).ToArray();
        Assert.Equal("accepted", names[0]);
        Assert.Equal("done", names[^1]);
        Assert.True(names.Count(n => n == "delta") > 3, "the reply arrives in several pieces");
        Assert.DoesNotContain("crisis", names);
        var reply = events.AssembleReply();
        Assert.False(string.IsNullOrWhiteSpace(reply));

        var detail = await (await session.Client.GetAsync($"/api/conversations/{conversation}")).ReadJsonAsync();
        var messages = detail.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Bugün çok yorgunum", messages[0].GetProperty("text").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal(reply, messages[1].GetProperty("text").GetString());
        Assert.Equal("complete", messages[1].GetProperty("completion").GetString());
        Assert.Equal(events[0].Data.GetProperty("userMessageId").GetGuid(), messages[0].GetProperty("id").GetGuid());
        Assert.Equal(events[^1].Data.GetProperty("assistantMessageId").GetGuid(), messages[1].GetProperty("id").GetGuid());
        Assert.False(messages[1].TryGetProperty("flags", out var flags) && flags.ValueKind != JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task TheSameClientMessageId_PlaysTheStoredReplyAgain_WithoutNewRows()
    {
        var session = await NewOnboardedSessionAsync();
        var conversation = await session.StartConversationAsync();
        var clientId = Guid.NewGuid();

        var (_, first) = await session.SendAsync(conversation, "Merhaba", clientId);
        var (_, second) = await session.SendAsync(conversation, "Merhaba", clientId);

        Assert.Equal(first.AssembleReply(), second.AssembleReply());
        Assert.Equal(first[^1].Data.GetProperty("assistantMessageId").GetGuid(), second[^1].Data.GetProperty("assistantMessageId").GetGuid());

        await using var db = TestDatabase.NewContext();
        Assert.Equal(2, await db.Messages.CountAsync(m => m.ConversationId == conversation));
    }

    [PostgresFact]
    public async Task ParallelDuplicates_OfOneClientMessageId_LeaveExactlyOneUserMessageAndOneReply()
    {
        var session = await NewOnboardedSessionAsync();
        var conversation = await session.StartConversationAsync();
        var clientId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.SendAsync(conversation, "Merhaba", clientId)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Response.StatusCode));
        Assert.All(results, r => Assert.Equal("done", r.Events[^1].Name));
        Assert.Single(results.Select(r => r.Events[^1].Data.GetProperty("assistantMessageId").GetGuid()).Distinct());

        await using var db = TestDatabase.NewContext();
        Assert.Equal(2, await db.Messages.CountAsync(m => m.ConversationId == conversation));
    }

    [PostgresFact]
    public async Task ACrisisMessage_IsFlagged_GetsTheSafeReply_AndSkipsTheNormalResponder()
    {
        var session = await NewOnboardedSessionAsync();
        var conversation = await session.StartConversationAsync();

        var (_, events) = await session.SendAsync(conversation, $"{MarkerCrisisDetector.Marker} bugün");
        Assert.Equal(["accepted", "crisis"], events.Take(2).Select(e => e.Name));
        Assert.Equal("done", events[^1].Name);
        Assert.Equal(CrisisResponseProvider.Reply, events.AssembleReply().TrimEnd());

        var detail = await (await session.Client.GetAsync($"/api/conversations/{conversation}")).ReadJsonAsync();
        var messages = detail.GetProperty("messages").EnumerateArray().ToArray();
        Assert.All(messages, m => Assert.Equal("crisis", m.GetProperty("flags").EnumerateArray().Single().GetString()));

        await using var db = TestDatabase.NewContext();
        Assert.Equal(2, await db.Messages.CountAsync(m => m.ConversationId == conversation && m.IsCrisis));
    }

    [PostgresFact]
    public async Task Messages_AreRejectedBeforeAccepted_WithPlainHttpErrors()
    {
        var session = await NewOnboardedSessionAsync();
        var conversation = await session.StartConversationAsync();

        var empty = await session.SendAsync(conversation, "   ");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.Response.StatusCode);

        var tooLong = await session.SendAsync(conversation, new string('a', 2001));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.Response.StatusCode);
        Assert.Equal("too-long", (await tooLong.Response.ReadJsonAsync()).GetProperty("code").GetString());

        var unknown = await session.SendAsync(Guid.NewGuid(), "Merhaba");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Response.StatusCode);

        await using var db = TestDatabase.NewContext();
        Assert.Equal(0, await db.Messages.CountAsync(m => m.ConversationId == conversation));
    }

    [PostgresFact]
    public async Task AnEndedConversation_Is409_AndEndingTwiceIsFine()
    {
        var session = await NewOnboardedSessionAsync();
        var conversation = await session.StartConversationAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync($"/api/conversations/{conversation}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync($"/api/conversations/{conversation}/end", null)).StatusCode);

        var (response, _) = await session.SendAsync(conversation, "Merhaba");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("conversation-ended", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task StartingANewConversation_EndsThePreviousOne_AndTheListIsNewestFirstWithoutContent()
    {
        var session = await NewOnboardedSessionAsync();
        var first = await session.StartConversationAsync();
        await session.SendAsync(first, "Merhaba");
        var second = await session.StartConversationAsync();

        var list = await (await session.Client.GetAsync("/api/conversations")).ReadJsonAsync();
        var items = list.EnumerateArray().ToArray();
        Assert.Equal([second, first], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("endedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, items[1].GetProperty("endedAt").ValueKind);
        Assert.Equal(2, items[1].GetProperty("messageCount").GetInt32());
        Assert.DoesNotContain("Merhaba", list.GetRawText(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ParticipantsNeverSeeEachOthersConversations()
    {
        var owner = await NewOnboardedSessionAsync();
        var other = await NewOnboardedSessionAsync();
        var conversation = await owner.StartConversationAsync();
        await owner.SendAsync(conversation, "Gizli içerik");

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/conversations/{conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsync($"/api/conversations/{conversation}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(conversation, "Merhaba")).Response.StatusCode);
        Assert.DoesNotContain(conversation.ToString(), await (await other.Client.GetAsync("/api/conversations")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ADroppedConnection_KeepsThePartialReplyAsStopped_AndTheSameIdRegeneratesIt()
    {
        using var slow = host.NewFactory(new() { ["Chat:Fake:TokenDelayMs"] = "120", ["Chat:Fake:FirstTokenDelayMs"] = "0" });
        var participant = await host.CreateParticipantAsync();
        var session = await slow.SignInAsync(participant);
        await session.OnboardAsync();
        var conversation = await session.StartConversationAsync();
        var clientId = Guid.NewGuid();

        using (var cts = new CancellationTokenSource())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversation}/messages")
            {
                Content = IntegrationClient.Json(JsonSerializer.Serialize(new { text = "Merhaba", clientMessageId = clientId })),
            };
            using var response = await session.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var deltas = 0;
            while (deltas < 3 && await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("event: delta", StringComparison.Ordinal))
                {
                    deltas++;
                }
            }

            Assert.Equal(3, deltas);
            await cts.CancelAsync(); // the "stop" button / a closed tab
        }

        // The server notices the closed connection and keeps what was said, marked as stopped.
        string? completion = null;
        for (var i = 0; i < 50 && completion != "stopped"; i++)
        {
            await Task.Delay(100);
            var detail = await (await session.Client.GetAsync($"/api/conversations/{conversation}")).ReadJsonAsync();
            var assistant = detail.GetProperty("messages").EnumerateArray().FirstOrDefault(m => m.GetProperty("role").GetString() == "assistant");
            completion = assistant.ValueKind == JsonValueKind.Undefined ? null : assistant.GetProperty("completion").GetString();
        }

        Assert.Equal("stopped", completion);

        // Sending the same id again produces a complete reply in place of the stopped one (no duplicate rows).
        var (_, again) = await session.SendAsync(conversation, "Merhaba", clientId);
        Assert.Equal("done", again[^1].Name);
        await using var db = TestDatabase.NewContext();
        var stored = await db.Messages.AsNoTracking().Where(m => m.ConversationId == conversation).OrderBy(m => m.CreatedAt).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(MessageCompletion.Complete, stored[1].Completion);
    }

    [PostgresFact]
    public async Task TheDatabase_RefusesASecondActiveConversationAndBadValues_EvenWithoutTheApplication()
    {
        var participant = await host.CreateParticipantAsync(ParticipantStatus.Active);
        await using var db = TestDatabase.NewContext();
        var now = DateTimeOffset.UtcNow;
        db.Conversations.Add(new() { Id = Guid.CreateVersion7(), ParticipantId = participant.Id, StartedAt = now });
        await db.SaveChangesAsync();

        db.Conversations.Add(new() { Id = Guid.CreateVersion7(), ParticipantId = participant.Id, StartedAt = now });
        var duplicate = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (duplicate.InnerException as PostgresException)?.SqlState);
        db.ChangeTracker.Clear();

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var bad = new NpgsqlCommand("UPDATE public.participants SET status = 'Nonsense' WHERE id = @id", connection);
        bad.Parameters.AddWithValue("id", participant.Id);
        var check = await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, check.SqlState);
        Assert.Equal("ck_participants_status", check.ConstraintName);
    }

    [PostgresFact]
    public async Task AnAccessTokenForAnUnknownParticipant_Is401()
    {
        var session = await host.Factory.SignInAsync(await host.CreateParticipantAsync());
        var ghost = host.Factory.Services.GetRequiredService<Chatbot.Application.Abstractions.IAccessTokenIssuer>().Issue(Guid.NewGuid()).Token;
        using var client = host.Factory.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ghost);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/me")).StatusCode);
    }
}
