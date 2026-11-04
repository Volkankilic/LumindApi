using System.Runtime.CompilerServices;
using Chatbot.Application;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Dev;

namespace Chatbot.Tests.Unit;

/// <summary>An in-memory IChatStore with the same rules as the database one (unique client message id, ownership).</summary>
internal sealed class MemoryChatStore : IChatStore
{
    public Guid OwnerId { get; } = Guid.NewGuid();

    public Conversation Conversation { get; } = new() { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow };

    public List<Message> Messages { get; } = [];

    private long _tick;

    public MemoryChatStore() => Conversation.ParticipantId = OwnerId;

    private DateTimeOffset Next() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(++_tick);

    public Task<Conversation?> FindOwnedConversationAsync(Guid participantId, Guid conversationId, CancellationToken ct) =>
        Task.FromResult<Conversation?>(participantId == OwnerId && conversationId == Conversation.Id ? Conversation : null);

    public Task<(Message Message, bool Existed)> AddUserMessageAsync(Guid conversationId, Guid clientMessageId, string text, CancellationToken ct)
    {
        var existing = Messages.FirstOrDefault(m => m.ClientMessageId == clientMessageId);
        if (existing is not null)
        {
            return Task.FromResult((existing, true));
        }

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = text,
            CreatedAt = Next(),
            ClientMessageId = clientMessageId,
        };
        Messages.Add(message);
        return Task.FromResult((message, false));
    }

    public Task<Message?> FindReplyAsync(Message userMessage, CancellationToken ct)
    {
        var nextUser = Messages.Where(m => m.Role == MessageRole.User && m.CreatedAt > userMessage.CreatedAt).Select(m => (DateTimeOffset?)m.CreatedAt).Min();
        return Task.FromResult(Messages
            .Where(m => m.Role == MessageRole.Assistant && m.CreatedAt >= userMessage.CreatedAt && (nextUser is null || m.CreatedAt < nextUser))
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefault());
    }

    public Task DeleteMessageAsync(Guid messageId, CancellationToken ct)
    {
        Messages.RemoveAll(m => m.Id == messageId);
        return Task.CompletedTask;
    }

    public Task MarkCrisisAsync(Guid messageId, CancellationToken ct)
    {
        Messages.Single(m => m.Id == messageId).IsCrisis = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Message>> GetRecentAsync(Guid conversationId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Message>>(Messages.OrderBy(m => m.CreatedAt).TakeLast(take).ToList());

    public Task<Guid> AddAssistantMessageAsync(Guid conversationId, string text, MessageCompletion completion, bool isCrisis, CancellationToken ct)
    {
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            Role = MessageRole.Assistant,
            Content = text,
            CreatedAt = Next(),
            Completion = completion,
            IsCrisis = isCrisis,
        };
        Messages.Add(message);
        return Task.FromResult(message.Id);
    }
}

/// <summary>A responder that records its calls and replays scripted chunks (optionally failing or waiting).</summary>
internal sealed class ScriptedResponder(params string[] chunks) : IChatResponder
{
    public int Calls { get; private set; }

    public List<ConversationContext> Contexts { get; } = [];

    public Exception? FailAfterChunks { get; set; }

    /// <summary>When set, the responder waits here after the first chunk (to cancel a stream mid reply).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async IAsyncEnumerable<string> StreamReplyAsync(ConversationContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        Calls++;
        Contexts.Add(context);
        for (var i = 0; i < chunks.Length; i++)
        {
            yield return chunks[i];
            if (i == 0 && Gate is not null)
            {
                await Gate.Task.WaitAsync(ct);
            }
        }

        if (FailAfterChunks is not null)
        {
            throw FailAfterChunks;
        }

        await Task.CompletedTask;
    }
}

public class ChatServiceTests
{
    private static readonly Guid ClientId1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private sealed class Rig
    {
        public MemoryChatStore Store { get; } = new();

        public ScriptedResponder Responder { get; set; } = new("Merhaba ", "dünya ", "güzel.");

        public ICrisisDetector Detector { get; set; } = new NoopCrisisDetector();

        public IDevChatTriggers Triggers { get; set; } = new NoopDevChatTriggers();

        public ChatOptions Options { get; } = new() { MaxMessageLength = 2000, ContextMessages = 20 };

        public ChatService Service() => new(
            Store, Detector, new CrisisResponseProvider(), Responder, new NoopPiiRedactor(), Triggers, new ConversationLocks(), Options);

        public IAsyncEnumerable<ChatEvent> Send(string? text, Guid? clientId = null, CancellationToken ct = default) =>
            Service().SendAsync(Store.OwnerId, Store.Conversation.Id, text, clientId ?? ClientId1, ct);
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }

    private static string Names(IEnumerable<ChatEvent> events) =>
        string.Join(",", events.Select(e => e switch
        {
            ChatEvent.Accepted => "accepted",
            ChatEvent.Crisis => "crisis",
            ChatEvent.Delta => "delta",
            ChatEvent.Done => "done",
            ChatEvent.Error => "error",
            _ => "?",
        }));

    // ---------------------------------------------------------------- the normal path

    [Fact]
    public async Task NormalFlow_IsAcceptedThenDeltasThenDone_AndBothMessagesAreStored()
    {
        var rig = new Rig();
        var events = await Collect(rig.Send("Bugün zor bir gün"));

        Assert.Equal("accepted,delta,delta,delta,done", Names(events));
        Assert.Equal("Merhaba dünya güzel.", string.Concat(events.OfType<ChatEvent.Delta>().Select(d => d.Text)));

        var user = rig.Store.Messages.Single(m => m.Role == MessageRole.User);
        var assistant = rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal(user.Id, events.OfType<ChatEvent.Accepted>().Single().UserMessageId);
        Assert.Equal(assistant.Id, events.OfType<ChatEvent.Done>().Single().AssistantMessageId);
        Assert.Equal(MessageCompletion.Complete, assistant.Completion);
        Assert.Equal("Merhaba dünya güzel.", assistant.Content);
        Assert.False(assistant.IsCrisis);
        Assert.Equal(ClientId1, user.ClientMessageId);
    }

    [Fact]
    public async Task TheUserMessageIsStoredBeforeAccepted_AndTrimmed()
    {
        var rig = new Rig();
        await using var enumerator = rig.Send("  merhaba  ").GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<ChatEvent.Accepted>(enumerator.Current);
        Assert.Equal("merhaba", rig.Store.Messages.Single().Content);
        Assert.Equal(0, rig.Responder.Calls); // the responder has not run yet
    }

    [Fact]
    public async Task TheContext_HoldsTheLatestMessagesWithTheNewOneLast()
    {
        var rig = new Rig();
        await Collect(rig.Send("ilk mesaj", Guid.NewGuid()));
        await Collect(rig.Send("ikinci mesaj", Guid.NewGuid()));

        var context = rig.Responder.Contexts.Last();
        Assert.Equal("ikinci mesaj", context.Messages.Last().Text);
        Assert.Equal(MessageRole.User, context.Messages.Last().Role);
        Assert.Equal(3, context.Messages.Count); // user, assistant, user
    }

    // ---------------------------------------------------------------- errors BEFORE accepted are thrown, nothing is stored

    [Fact]
    public async Task EmptyOrBlankText_IsEmptyMessage422_BeforeAnything()
    {
        var rig = new Rig();
        foreach (var text in new[] { null, "", "   \n\t " })
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => Collect(rig.Send(text)));
            Assert.Equal(ProblemCodes.EmptyMessage, ex.Code);
            Assert.Equal(422, ex.StatusCode);
        }

        Assert.Empty(rig.Store.Messages);
    }

    [Fact]
    public async Task TooLongText_IsTooLong422_AndTheLimitItselfIsAllowed()
    {
        var rig = new Rig();
        var ex = await Assert.ThrowsAsync<AppException>(() => Collect(rig.Send(new string('a', 2001))));
        Assert.Equal(ProblemCodes.TooLong, ex.Code);
        Assert.Equal(422, ex.StatusCode);
        Assert.Empty(rig.Store.Messages);

        var events = await Collect(rig.Send(new string('a', 2000), Guid.NewGuid()));
        Assert.Equal("done", Names(events.TakeLast(1)));
    }

    [Fact]
    public async Task SomeoneElsesConversationAndAMissingOne_AreTheSameNotFound()
    {
        var rig = new Rig();
        var service = rig.Service();
        var someoneElse = await Assert.ThrowsAsync<AppException>(() =>
            Collect(service.SendAsync(Guid.NewGuid(), rig.Store.Conversation.Id, "merhaba", ClientId1, default)));
        var missing = await Assert.ThrowsAsync<AppException>(() =>
            Collect(service.SendAsync(rig.Store.OwnerId, Guid.NewGuid(), "merhaba", ClientId1, default)));

        Assert.Equal(404, someoneElse.StatusCode);
        Assert.Equal(someoneElse.Code, missing.Code);
        Assert.Equal(ProblemCodes.NotFound, missing.Code);
        Assert.Equal(someoneElse.Message, missing.Message);
    }

    [Fact]
    public async Task AnEndedConversation_IsConversationEnded409_WithoutAcceptingTheMessage()
    {
        var rig = new Rig();
        rig.Store.Conversation.EndedAt = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<AppException>(() => Collect(rig.Send("merhaba")));
        Assert.Equal(ProblemCodes.ConversationEnded, ex.Code);
        Assert.Equal(409, ex.StatusCode);
        Assert.Empty(rig.Store.Messages);
    }

    [Fact]
    public async Task ADevTriggerThatFailsBeforeAccepted_StoresNothing()
    {
        var rig = new Rig { Triggers = new DevChatTriggers() };
        var ex = await Assert.ThrowsAsync<AppException>(() => Collect(rig.Send("/hata deneme")));
        Assert.Equal(503, ex.StatusCode);
        Assert.Empty(rig.Store.Messages);
        Assert.Equal(0, rig.Responder.Calls);
    }

    // ---------------------------------------------------------------- crisis: before any model

    [Fact]
    public async Task Crisis_IsAcceptedThenCrisisThenDeltasThenDone_TheResponderIsNeverCalled()
    {
        var rig = new Rig { Detector = new DevKeywordCrisisDetector() };
        var events = await Collect(rig.Send("artık yaşamak istemiyorum"));

        Assert.Equal("accepted", Names(events.Take(1)));
        Assert.Equal("crisis", Names(events.Skip(1).Take(1)));
        Assert.Equal("done", Names(events.TakeLast(1)));
        Assert.All(events.Skip(2).SkipLast(1), e => Assert.IsType<ChatEvent.Delta>(e));
        Assert.Equal(0, rig.Responder.Calls); // IChatResponder was NOT called

        Assert.Equal(CrisisResponseProvider.Reply.TrimEnd(), string.Concat(events.OfType<ChatEvent.Delta>().Select(d => d.Text)).TrimEnd());
        Assert.True(rig.Store.Messages.Single(m => m.Role == MessageRole.User).IsCrisis);
        Assert.True(rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant).IsCrisis);
    }

    [Fact]
    public async Task WithTheDefaultNoopDetector_NothingIsTreatedAsACrisis()
    {
        var rig = new Rig();
        var events = await Collect(rig.Send("intihar"));
        Assert.DoesNotContain(events, e => e is ChatEvent.Crisis);
        Assert.Equal(1, rig.Responder.Calls);
    }

    // ---------------------------------------------------------------- errors AFTER accepted are events

    [Fact]
    public async Task AFailureMidReply_IsAnErrorEvent_AndTheTextSoFarIsStoredAsIncomplete()
    {
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ", "dünya ") { FailAfterChunks = AppException.Unavailable() } };
        var events = await Collect(rig.Send("selam"));

        Assert.Equal("accepted,delta,delta,error", Names(events));
        var error = Assert.IsType<ChatEvent.Error>(events.Last());
        Assert.Equal("unavailable", error.Code);
        var assistant = rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal(MessageCompletion.Incomplete, assistant.Completion);
        Assert.Equal("Merhaba dünya", assistant.Content);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsErrorUnknown_WithoutDetail()
    {
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ") { FailAfterChunks = new InvalidOperationException("secret detail: select * from") } };
        var events = await Collect(rig.Send("selam"));
        var error = Assert.IsType<ChatEvent.Error>(events.Last());
        Assert.Equal("unknown", error.Code);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureBeforeTheFirstWord_StoresNoEmptyAssistantMessage()
    {
        var rig = new Rig { Responder = new ScriptedResponder { FailAfterChunks = AppException.Unavailable() } };
        var events = await Collect(rig.Send("selam"));
        Assert.Equal("accepted,error", Names(events));
        Assert.DoesNotContain(rig.Store.Messages, m => m.Role == MessageRole.Assistant);
    }

    // ---------------------------------------------------------------- cancellation: stored as Stopped

    [Fact]
    public async Task WhenTheClientLeavesMidReply_TheTextSoFarIsStoredAsStopped()
    {
        var gate = new TaskCompletionSource();
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ", "dünya ") { Gate = gate } };
        using var cts = new CancellationTokenSource();

        await using (var enumerator = rig.Send("selam", ct: cts.Token).GetAsyncEnumerator(cts.Token))
        {
            Assert.True(await enumerator.MoveNextAsync()); // accepted
            Assert.True(await enumerator.MoveNextAsync()); // first delta ("Merhaba ")
            Assert.IsType<ChatEvent.Delta>(enumerator.Current);
            await cts.CancelAsync(); // the client closes the connection
        } // disposing the enumerator is what the controller does when the write fails

        var assistant = rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal(MessageCompletion.Stopped, assistant.Completion);
        Assert.Equal("Merhaba", assistant.Content);
    }

    [Fact]
    public async Task WhenTheResponderIsCancelledWhileWaiting_TheTextSoFarIsStoredAsStopped_AndNoErrorEventIsSent()
    {
        var gate = new TaskCompletionSource();
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ", "dünya ") { Gate = gate } };
        using var cts = new CancellationTokenSource();

        var events = new List<ChatEvent>();
        var consume = Task.Run(async () =>
        {
            await foreach (var e in rig.Send("selam", ct: cts.Token))
            {
                events.Add(e);
            }
        });
        while (events.Count < 2)
        {
            await Task.Delay(5);
        }

        await cts.CancelAsync();
        await consume;

        Assert.DoesNotContain(events, e => e is ChatEvent.Error or ChatEvent.Done);
        Assert.Equal(MessageCompletion.Stopped, rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant).Completion);
    }

    [Fact]
    public async Task NothingIsStoredWhenTheClientLeavesBeforeAnyWord()
    {
        var rig = new Rig();
        await using (var enumerator = rig.Send("selam").GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync()); // accepted, then the client goes away
        }

        Assert.DoesNotContain(rig.Store.Messages, m => m.Role == MessageRole.Assistant);
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task TheSameClientMessageId_DoesNotCreateASecondUserMessage_AndACompleteReplyIsPlayedAgain()
    {
        var rig = new Rig();
        var first = await Collect(rig.Send("selam"));
        var second = await Collect(rig.Send("selam"));

        Assert.Single(rig.Store.Messages, m => m.Role == MessageRole.User);
        Assert.Single(rig.Store.Messages, m => m.Role == MessageRole.Assistant);
        Assert.Equal(1, rig.Responder.Calls); // not generated again

        Assert.Equal("accepted,delta,delta,delta,done", Names(second));
        Assert.Equal(
            first.OfType<ChatEvent.Accepted>().Single().UserMessageId,
            second.OfType<ChatEvent.Accepted>().Single().UserMessageId);
        Assert.Equal(
            first.OfType<ChatEvent.Done>().Single().AssistantMessageId,
            second.OfType<ChatEvent.Done>().Single().AssistantMessageId);
        Assert.Equal(
            string.Concat(first.OfType<ChatEvent.Delta>().Select(d => d.Text)).TrimEnd(),
            string.Concat(second.OfType<ChatEvent.Delta>().Select(d => d.Text)).TrimEnd());
    }

    [Fact]
    public async Task ACompleteCrisisReply_IsReplayedWithTheCrisisEvent()
    {
        var rig = new Rig { Detector = new DevKeywordCrisisDetector() };
        await Collect(rig.Send("intihar"));
        var replay = await Collect(rig.Send("intihar"));
        Assert.Equal("crisis", Names(replay.Skip(1).Take(1)));
        Assert.Equal(1, rig.Store.Messages.Count(m => m.Role == MessageRole.Assistant));
    }

    [Fact]
    public async Task AnIncompleteReply_IsDeletedAndProducedAgain_WithoutASecondUserMessage()
    {
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ", "dünya ") { FailAfterChunks = AppException.Unavailable() } };
        await Collect(rig.Send("selam"));
        Assert.Equal(MessageCompletion.Incomplete, rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant).Completion);

        rig.Responder = new ScriptedResponder("Tamam ", "oldu.");
        var retry = await Collect(rig.Send("selam"));

        Assert.Equal("accepted,delta,delta,done", Names(retry));
        Assert.Single(rig.Store.Messages, m => m.Role == MessageRole.User);
        var assistant = rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal(MessageCompletion.Complete, assistant.Completion);
        Assert.Equal("Tamam oldu.", assistant.Content);
    }

    [Fact]
    public async Task AStoppedReply_IsDeletedAndProducedAgain()
    {
        var gate = new TaskCompletionSource();
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ", "dünya ") { Gate = gate } };
        using (var cts = new CancellationTokenSource())
        {
            await using var enumerator = rig.Send("selam", ct: cts.Token).GetAsyncEnumerator(cts.Token);
            await enumerator.MoveNextAsync();
            await enumerator.MoveNextAsync();
            await cts.CancelAsync();
        }

        Assert.Equal(MessageCompletion.Stopped, rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant).Completion);

        rig.Responder = new ScriptedResponder("Yeni ", "yanıt.");
        await Collect(rig.Send("selam"));
        var assistant = rig.Store.Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal(MessageCompletion.Complete, assistant.Completion);
        Assert.Equal("Yeni yanıt.", assistant.Content);
    }

    [Fact]
    public async Task TwoConcurrentRequestsWithTheSameId_OneUserMessageAndOneReply()
    {
        var rig = new Rig { Responder = new ScriptedResponder("Bir ", "iki ", "üç.") };
        var service = rig.Service();
        var a = Collect(service.SendAsync(rig.Store.OwnerId, rig.Store.Conversation.Id, "selam", ClientId1, default));
        var b = Collect(service.SendAsync(rig.Store.OwnerId, rig.Store.Conversation.Id, "selam", ClientId1, default));
        var (resultA, resultB) = (await a, await b);

        Assert.Single(rig.Store.Messages, m => m.Role == MessageRole.User);
        Assert.Single(rig.Store.Messages, m => m.Role == MessageRole.Assistant);
        Assert.Equal(1, rig.Responder.Calls);
        Assert.Equal("done", Names(resultA.TakeLast(1)));
        Assert.Equal("done", Names(resultB.TakeLast(1)));
    }

    [Fact]
    public async Task ARetryOfAnEarlierMessage_IsNotAnsweredWithALaterReply()
    {
        // Message A got no reply (the client left at once). Message B was answered. Retrying A must produce A's own
        // reply, not take B's.
        var rig = new Rig();
        var idA = Guid.NewGuid();
        await using (var enumerator = rig.Send("birinci", idA).GetAsyncEnumerator())
        {
            await enumerator.MoveNextAsync();
        }

        await Collect(rig.Send("ikinci", Guid.NewGuid()));
        Assert.Equal(1, rig.Responder.Calls);

        var retry = await Collect(rig.Send("birinci", idA));
        Assert.Equal("done", Names(retry.TakeLast(1)));
        Assert.Equal(2, rig.Responder.Calls); // A was answered on its own
    }
}

public class ChatRulesTests
{
    private static Message Msg(MessageRole role, string text, MessageCompletion? completion = null) =>
        new() { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Role = role, Content = text, Completion = completion };

    [Theory]
    [InlineData(false, null, false, IdempotencyDecision.Process)]
    [InlineData(true, MessageCompletion.Complete, true, IdempotencyDecision.Replay)]
    [InlineData(true, MessageCompletion.Stopped, true, IdempotencyDecision.Regenerate)]
    [InlineData(true, MessageCompletion.Incomplete, true, IdempotencyDecision.Regenerate)]
    [InlineData(true, null, false, IdempotencyDecision.Regenerate)]
    public void Decide_FollowsTheIdempotencyRules(bool existed, MessageCompletion? completion, bool hasReply, IdempotencyDecision expected) =>
        Assert.Equal(expected, ChatRules.Decide(existed, completion, hasReply));

    [Fact]
    public void ValidateText_Trims_AndEnforcesTheLimits()
    {
        Assert.Equal("merhaba", ChatRules.ValidateText("  merhaba \n", 2000));
        Assert.Equal(ProblemCodes.EmptyMessage, Assert.Throws<AppException>(() => ChatRules.ValidateText(" ", 2000)).Code);
        Assert.Equal(ProblemCodes.TooLong, Assert.Throws<AppException>(() => ChatRules.ValidateText(new string('x', 11), 10)).Code);
        Assert.Equal(new string('x', 10), ChatRules.ValidateText(new string('x', 10), 10));
    }

    [Fact]
    public void BuildContext_KeepsUserMessagesAndOnlyCompleteAssistantMessages()
    {
        var messages = new[]
        {
            Msg(MessageRole.User, "1"),
            Msg(MessageRole.Assistant, "a-complete", MessageCompletion.Complete),
            Msg(MessageRole.User, "2"),
            Msg(MessageRole.Assistant, "a-stopped", MessageCompletion.Stopped),
            Msg(MessageRole.User, "3"),
            Msg(MessageRole.Assistant, "a-incomplete", MessageCompletion.Incomplete),
            Msg(MessageRole.User, "4"),
        };
        var texts = ChatRules.BuildContext(messages, 20).Select(m => m.Text).ToArray();
        Assert.Equal(["1", "a-complete", "2", "3", "4"], texts);
    }

    [Fact]
    public void BuildContext_TakesOnlyTheLatestN()
    {
        var messages = Enumerable.Range(1, 30).Select(i => Msg(MessageRole.User, i.ToString())).ToList();
        var context = ChatRules.BuildContext(messages, 20);
        Assert.Equal(20, context.Count);
        Assert.Equal("30", context.Last().Text);
        Assert.Equal("11", context.First().Text);
    }

    [Fact]
    public void Chunks_AreWordsThatKeepTheirTrailingSpace_AndJoinBackToTheOriginal()
    {
        const string text = "Buradayım ve seni dinliyorum. Acele yok.";
        var chunks = ChatRules.Chunks(text);
        Assert.Equal("Buradayım ", chunks[0]);
        Assert.Equal(text, string.Concat(chunks));
        Assert.Equal(["tek"], ChatRules.Chunks("tek"));
    }
}

public class ConversationLocksTests
{
    [Fact]
    public async Task TheSameConversation_RunsOneAtATime()
    {
        var locks = new ConversationLocks();
        var id = Guid.NewGuid();
        var inside = 0;
        var maxInside = 0;

        async Task Work()
        {
            using var turn = await locks.AcquireAsync(id, default);
            var now = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, now);
            await Task.Delay(15);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Work()));
        Assert.Equal(1, maxInside);
    }

    [Fact]
    public async Task DifferentConversations_DoNotWaitForEachOther()
    {
        var locks = new ConversationLocks();
        using var first = await locks.AcquireAsync(Guid.NewGuid(), default);
        using var second = await locks.AcquireAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ACancelledWait_ReleasesNothingItDidNotHold()
    {
        var locks = new ConversationLocks();
        var id = Guid.NewGuid();
        var holder = await locks.AcquireAsync(id, default);
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireAsync(id, cts.Token));
        holder.Dispose();

        using var again = await locks.AcquireAsync(id, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(again);
    }
}
