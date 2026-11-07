using Chatbot.Domain;
using Chatbot.Domain.Entities;

namespace Chatbot.Application.Chat;

/// <summary>
/// What the stream tells the client. Order: Accepted, [Crisis], Delta*, Done, or Error at any point after Accepted.
/// Failures BEFORE Accepted are not events: they are thrown as <see cref="AppException"/> (plain HTTP errors).
/// </summary>
public abstract record ChatEvent
{
    public sealed record Accepted(Guid UserMessageId) : ChatEvent;

    public sealed record Crisis : ChatEvent;

    public sealed record Delta(string Text) : ChatEvent;

    public sealed record Done(Guid AssistantMessageId) : ChatEvent;

    /// <param name="Code">unavailable | rate-limited | too-long | unknown</param>
    public sealed record Error(string Code, int? RetryAfterSeconds = null) : ChatEvent;
}

public sealed record ContextMessage(MessageRole Role, string Text);

/// <summary>Only the fake responder (and the Development trigger decorator) look at the pacing hint.</summary>
public enum ReplyPacing { Normal, Fast, Slow }

public sealed record ConversationContext(
    IReadOnlyList<ContextMessage> Messages,
    Guid ClientMessageId,
    ReplyPacing Pacing = ReplyPacing.Normal);

/// <summary>Produces the assistant reply as text chunks (words). Phase 6 adds a real model behind this interface.</summary>
public interface IChatResponder
{
    IAsyncEnumerable<string> StreamReplyAsync(ConversationContext context, CancellationToken ct);
}

/// <summary>
/// What kind of crisis wording was recognised. It lives in MEMORY only, for the current request: it is never stored,
/// logged, audited, sent in an API answer or shown in the client (ADR 008, section 6). Its one purpose is that a later,
/// clinically approved step can choose the reply text by category without rebuilding the pipeline.
/// </summary>
public enum CrisisCategory
{
    None,
    DeathWish,
    Intent,
    Means,
    Hopelessness,
    SelfHarm,
    HarmToOthers,
    MedicalEmergency,

    /// <summary>The detector failed, so the message is treated as a crisis without knowing why.</summary>
    Unknown,
}

/// <summary>The result of looking at one message. Do not log it or persist it (see <see cref="CrisisCategory"/>).</summary>
public readonly record struct CrisisAssessment(bool IsCrisis, CrisisCategory Category)
{
    public static CrisisAssessment None { get; } = new(false, CrisisCategory.None);

    /// <summary>Fail safe: used when the detector itself failed.</summary>
    public static CrisisAssessment Unavailable { get; } = new(true, CrisisCategory.Unknown);

    public static CrisisAssessment Of(CrisisCategory category) => new(true, category);
}

/// <summary>
/// Decides whether a message is a crisis. It runs BEFORE the responder: a crisis never reaches it.
/// </summary>
public interface ICrisisDetector
{
    CrisisAssessment Assess(string text);

    bool IsCrisis(string text) => Assess(text).IsCrisis;

    /// <summary>
    /// True only for the real, rule based detector. Production refuses to start without one (the no-op and the
    /// Development keyword stand-in answer false).
    /// </summary>
    bool IsProductionGrade => false;
}

/// <summary>The fixed, safe reply streamed instead of a model reply during a crisis.</summary>
public interface ICrisisResponseProvider
{
    /// <summary>Today every category gets the same reply; the assessment is here so a category can pick the text later.</summary>
    string GetReply(CrisisAssessment assessment);
}

/// <summary>Phase 4 masks personal data here before text leaves for a model. Today it passes the text through.</summary>
public interface IPiiRedactor
{
    string Redact(string text);
}

/// <summary>Development only: trigger words (/hata, /limit) that fail a message before it is accepted. Production uses a no-op.</summary>
public interface IDevChatTriggers
{
    /// <exception cref="AppException">When the trigger asks for a failure before the message is accepted.</exception>
    void BeforeAccept(string text, Guid clientMessageId);
}

/// <summary>Persistence port of the send pipeline.</summary>
public interface IChatStore
{
    /// <summary>The conversation when it belongs to the participant, otherwise null (the same for "not yours" and "does not exist").</summary>
    Task<Conversation?> FindOwnedConversationAsync(Guid participantId, Guid conversationId, CancellationToken ct);

    /// <summary>Stores the user message. When the client message id exists already, returns the stored one (Existed = true).</summary>
    Task<(Message Message, bool Existed)> AddUserMessageAsync(
        Guid conversationId,
        Guid clientMessageId,
        string text,
        CancellationToken ct);

    /// <summary>The assistant message that answers the given user message (the first one after it, before the next user message).</summary>
    Task<Message?> FindReplyAsync(Message userMessage, CancellationToken ct);

    Task DeleteMessageAsync(Guid messageId, CancellationToken ct);

    /// <summary>
    /// Flags the user message as a crisis and writes the content-free <c>crisis.detected</c> audit entry (participant,
    /// conversation, time: no text, no phrase, no category). The caller flags a message only once, so there is one entry per message.
    /// A failure to audit is logged without content and never stops the safe flow.
    /// </summary>
    Task MarkCrisisAsync(Guid participantId, Guid conversationId, Guid messageId, CancellationToken ct);

    /// <summary>The latest messages of the conversation, oldest first.</summary>
    Task<IReadOnlyList<Message>> GetRecentAsync(Guid conversationId, int take, CancellationToken ct);

    Task<Guid> AddAssistantMessageAsync(
        Guid conversationId,
        string text,
        MessageCompletion completion,
        bool isCrisis,
        CancellationToken ct);
}

public interface IChatService
{
    /// <summary>
    /// Runs the send pipeline. Anything that goes wrong before the first event (<see cref="ChatEvent.Accepted"/>)
    /// is thrown as <see cref="AppException"/> when the first element is requested.
    /// </summary>
    IAsyncEnumerable<ChatEvent> SendAsync(
        Guid participantId,
        Guid conversationId,
        string? text,
        Guid clientMessageId,
        CancellationToken ct);
}
