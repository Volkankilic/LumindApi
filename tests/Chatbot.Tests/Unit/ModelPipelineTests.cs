using Chatbot.Application;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Chat.Crisis;
using Chatbot.Infrastructure.Dev;

namespace Chatbot.Tests.Unit;

/// <summary>
/// Phase 6a, the send pipeline around a model: crisis stays in front of it, the fallback reply is a marked, kind, stored reply, the
/// prompt version is stored, and what a model may see as context. No network: the responder is scripted.
/// </summary>
public class ModelPipelineTests
{
    private const string PromptVersion = "v1+abcd1234";

    private sealed class MaskingRedactor : IPiiRedactor
    {
        public string Redact(string text) => text.Replace("ornek@example.com", "[E-POSTA]", StringComparison.Ordinal);
    }

    private sealed class Rig
    {
        public MemoryChatStore Store { get; } = new();

        public ScriptedResponder Responder { get; set; } = new("Merhaba ", "dünya.") { PromptVersion = PromptVersion };

        public ICrisisDetector Detector { get; set; } = new NoopCrisisDetector();

        public IPiiRedactor Redactor { get; set; } = new NoopPiiRedactor();

        public async Task<List<ChatEvent>> Send(string text, Guid? clientId = null)
        {
            var service = new ChatService(
                Store, Detector, new CrisisResponseProvider(), Responder, Redactor, new NoopDevChatTriggers(), new ConversationLocks(), new ChatOptions());
            var events = new List<ChatEvent>();
            await foreach (var e in service.SendAsync(Store.OwnerId, Store.Conversation.Id, text, clientId ?? Guid.NewGuid(), default))
            {
                events.Add(e);
            }

            return events;
        }

        public Message LastReply() => Store.Messages.Last(m => m.Role == MessageRole.Assistant);
    }

    private static string Joined(IEnumerable<ChatEvent> events) => string.Concat(events.OfType<ChatEvent.Delta>().Select(d => d.Text)).TrimEnd();

    // ---------------------------------------------------------------- the prompt version is stored

    [Fact]
    public async Task AModelReply_IsStoredWithThePromptVersionOfTheResponder()
    {
        var rig = new Rig();
        await rig.Send("merhaba");
        Assert.Equal(PromptVersion, rig.LastReply().SystemPromptVersion);
        Assert.Equal(MessageCompletion.Complete, rig.LastReply().Completion);
    }

    [Fact]
    public async Task AFakeReply_HasNoPromptVersion()
    {
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba.") };
        await rig.Send("merhaba");
        Assert.Null(rig.LastReply().SystemPromptVersion);
    }

    [Fact]
    public async Task ACutOffModelReply_KeepsThePromptVersion_AndIsNotAFallback()
    {
        var rig = new Rig { Responder = new ScriptedResponder("Merhaba ") { PromptVersion = PromptVersion, FailAfterChunks = new ModelUnavailableException(ModelFailure.Network) } };
        var events = await rig.Send("merhaba");

        Assert.IsType<ChatEvent.Error>(events.Last()); // words were shown already: no fallback text on top of them
        Assert.DoesNotContain("yanıt veremiyorum", Joined(events), StringComparison.Ordinal);
        var stored = rig.LastReply();
        Assert.Equal(MessageCompletion.Incomplete, stored.Completion);
        Assert.Equal(PromptVersion, stored.SystemPromptVersion);
    }

    // ---------------------------------------------------------------- the fallback reply

    [Fact]
    public async Task WhenTheModelIsUnavailableBeforeTheFirstWord_TheFallbackTextIsStreamedAsANormalReply()
    {
        var rig = new Rig { Responder = new ScriptedResponder("x") { PromptVersion = PromptVersion, FailBeforeFirstChunk = new ModelUnavailableException(ModelFailure.Timeout) } };
        var events = await rig.Send("merhaba");

        Assert.IsType<ChatEvent.Accepted>(events[0]);
        Assert.IsType<ChatEvent.Done>(events.Last());
        Assert.DoesNotContain(events, e => e is ChatEvent.Error or ChatEvent.Crisis);
        Assert.Equal(FallbackReply.Text, Joined(events));
    }

    [Fact]
    public async Task TheFallbackReply_IsStoredCompleteAndMarkedInSystemPromptVersion_NotAsACrisis()
    {
        var rig = new Rig { Responder = new ScriptedResponder("x") { PromptVersion = PromptVersion, FailBeforeFirstChunk = new ModelUnavailableException(ModelFailure.ServerError, 503) } };
        await rig.Send("merhaba");

        var stored = rig.LastReply();
        Assert.Equal(FallbackReply.Text, stored.Content);
        Assert.Equal(MessageCompletion.Complete, stored.Completion);
        Assert.Equal("fallback", stored.SystemPromptVersion);
        Assert.False(stored.IsCrisis);
        Assert.Equal("fallback", FallbackReply.Version);
    }

    [Fact]
    public void TheFallbackText_NamesTheEmergencyNumber_AndPromisesNothingMedical()
    {
        Assert.Contains("112", FallbackReply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("düzel", FallbackReply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(FallbackReply.Text.Length < 200);
    }

    [Fact]
    public async Task OnlyAModelFailureGetsTheFallback_AnyOtherFailureStaysAnUnknownErrorEvent()
    {
        var rig = new Rig { Responder = new ScriptedResponder("x") { FailBeforeFirstChunk = new InvalidOperationException("bug") } };
        var events = await rig.Send("merhaba");

        var error = Assert.IsType<ChatEvent.Error>(events.Last());
        Assert.Equal("unknown", error.Code);
        Assert.DoesNotContain(rig.Store.Messages, m => m.SystemPromptVersion == "fallback");
    }

    [Fact]
    public async Task ARetryOfAFallbackReply_ReplaysIt_WithoutAskingTheModelAgain()
    {
        var rig = new Rig { Responder = new ScriptedResponder("x") { FailBeforeFirstChunk = new ModelUnavailableException(ModelFailure.Network) } };
        var id = Guid.NewGuid();
        await rig.Send("merhaba", id);
        var replay = await rig.Send("merhaba", id);

        Assert.Equal(1, rig.Responder.Calls);
        Assert.Equal(FallbackReply.Text, Joined(replay));
        Assert.Single(rig.Store.Messages, m => m.Role == MessageRole.Assistant);
    }

    // ---------------------------------------------------------------- reasoning never reaches the participant

    [Fact]
    public async Task TheReasoningOfAModel_NeverReachesTheParticipant_NorTheStoredReply()
    {
        string Thought(string field, string text) =>
            "data: " + System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { delta = new Dictionary<string, string> { [field] = text } } } });
        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Sse(
            Thought("reasoning_content", "SECRET-THINKING Önce düşünelim"),
            Thought("reasoning", "SECRET-THINKING ikinci düşünce"),
            FakeLlmHandler.Data("Buradayım"),
            Thought("reasoning_content", "SECRET-THINKING üçüncü"),
            FakeLlmHandler.Data(" ve dinliyorum."),
            "data: [DONE]")));
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            BaseUrl = "https://llm.example.test/v1",
            AllowedHosts = ["llm.example.test"],
            Model = "test-model-x",
            ApiKey = "sk-TEST-KEY-123456789",
            ExtraBodyJson = """{"chat_template_kwargs":{"enable_thinking":false}}""",
        };
        // a REAL responder over a scripted network, behind the real send pipeline
        var real =new Chatbot.Infrastructure.Chat.Llm.OpenAiCompatibleChatResponder(
            Chatbot.Infrastructure.Chat.Llm.LlmHttpClient.Create(handler),
            options,
            Chatbot.Infrastructure.Chat.Llm.SystemPromptText.LoadEmbedded(),
            new ListLogger<Chatbot.Infrastructure.Chat.Llm.OpenAiCompatibleChatResponder>(),
            development: false);
        var store = new MemoryChatStore();
        var service = new ChatService(store, new NoopCrisisDetector(), new CrisisResponseProvider(), real, new NoopPiiRedactor(), new NoopDevChatTriggers(), new ConversationLocks(), new ChatOptions());
        var events = new List<ChatEvent>();
        await foreach (var e in service.SendAsync(store.OwnerId, store.Conversation.Id, "merhaba", Guid.NewGuid(), default))
        {
            events.Add(e);
        }

        Assert.Equal("Buradayım ve dinliyorum.", Joined(events));
        var stored = store.Messages.Last(m => m.Role == MessageRole.Assistant);
        Assert.Equal("Buradayım ve dinliyorum.", stored.Content);
        Assert.DoesNotContain(events.OfType<ChatEvent.Delta>(), d => d.Text.Contains("THINKING", StringComparison.Ordinal));
        Assert.DoesNotContain(store.Messages, m => m.Content.Contains("THINKING", StringComparison.Ordinal));
        Assert.Equal(real.PromptVersion, stored.SystemPromptVersion);
    }

    // ---------------------------------------------------------------- crisis stays in front of the model

    [Fact]
    public async Task ACrisisMessage_NeverReachesTheModel_AndItsReplyHasNoPromptVersion()
    {
        var rig = new Rig { Detector = RuleCrisisDetector.CreateDefault() };
        var events = await rig.Send("Bazen ölmek istiyorum");

        Assert.Equal(0, rig.Responder.Calls);
        Assert.Contains(events, e => e is ChatEvent.Crisis);
        Assert.Null(rig.LastReply().SystemPromptVersion);
        Assert.True(rig.LastReply().IsCrisis);
    }

    [Fact]
    public async Task AfterACrisis_TheNextModelContextHasNeitherTheCrisisMessageNorTheCrisisReply()
    {
        var rig = new Rig { Detector = RuleCrisisDetector.CreateDefault() };
        await rig.Send("Bugün çok yorgunum");
        await rig.Send("Bazen ölmek istiyorum");
        await rig.Send("Biraz daha iyiyim şimdi");

        Assert.Equal(2, rig.Responder.Calls); // the crisis turn did not call the model
        var texts = rig.Responder.Contexts[1].Messages.Select(m => m.Text).ToList();
        Assert.Contains("Bugün çok yorgunum", texts);
        Assert.Contains("Biraz daha iyiyim şimdi", texts);
        Assert.DoesNotContain(texts, t => t.Contains("ölmek", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(CrisisResponseProvider.Reply.Split('.')[0], StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADetectorThatBreaks_StillKeepsTheMessageAwayFromTheModel()
    {
        var rig = new Rig { Detector = new ThrowingDetector() };
        var events = await rig.Send("merhaba");

        Assert.Equal(0, rig.Responder.Calls);
        Assert.Contains(events, e => e is ChatEvent.Crisis);
    }

    private sealed class ThrowingDetector : ICrisisDetector
    {
        public CrisisAssessment Assess(string text) => throw new InvalidOperationException("detector broke");
    }

    // ---------------------------------------------------------------- what a model may see

    private static Message Msg(MessageRole role, string text, bool crisis = false, string? version = null) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Role = role,
        Content = text,
        CreatedAt = DateTimeOffset.UtcNow,
        Completion = role == MessageRole.Assistant ? MessageCompletion.Complete : null,
        IsCrisis = crisis,
        SystemPromptVersion = version,
    };

    [Fact]
    public void BuildContext_LeavesOutCrisisMessages_TheCrisisReply_AndFallbackReplies()
    {
        var messages = new[]
        {
            Msg(MessageRole.User, "normal-1"),
            Msg(MessageRole.Assistant, "model-1", version: PromptVersion),
            Msg(MessageRole.User, "crisis-user", crisis: true),
            Msg(MessageRole.Assistant, "crisis-reply", crisis: true),
            Msg(MessageRole.User, "normal-2"),
            Msg(MessageRole.Assistant, "fallback-reply", version: FallbackReply.Version),
            Msg(MessageRole.User, "normal-3"),
        };

        var texts = ChatRules.BuildContext(messages, 20).Select(m => m.Text).ToArray();

        Assert.Equal(["normal-1", "model-1", "normal-2", "normal-3"], texts);
    }

    [Fact]
    public async Task EveryTextThatGoesToTheModel_IsMaskedFirst()
    {
        var rig = new Rig { Redactor = new MaskingRedactor() };
        await rig.Send("Bana ornek@example.com adresinden yazabilirsin");
        await rig.Send("Teşekkürler");

        var sent = rig.Responder.Contexts.SelectMany(c => c.Messages).Select(m => m.Text).ToList();
        Assert.DoesNotContain(sent, t => t.Contains("ornek@example.com", StringComparison.Ordinal));
        Assert.Contains(sent, t => t.Contains("[E-POSTA]", StringComparison.Ordinal));
        // what is STORED is not touched by the masking (it protects the model path only)
        Assert.Contains(rig.Store.Messages, m => m.Content.Contains("ornek@example.com", StringComparison.Ordinal));
    }
}
