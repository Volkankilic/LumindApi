namespace Chatbot.Domain.Entities;

public class Message
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }

    /// <summary>Message text. Special-category data: never logged. Phase 4 encrypts this column.</summary>
    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsCrisis { get; set; }

    /// <summary>Client generated id of a user message (idempotency key within a conversation).</summary>
    public Guid? ClientMessageId { get; set; }

    /// <summary>Only for assistant messages.</summary>
    public MessageCompletion? Completion { get; set; }

    /// <summary>
    /// Only for assistant messages: the system prompt version that produced a model reply (e.g. "v1+3fa9c2d1"), or "fallback" for the fixed
    /// reply given when the model was unavailable. Null for fake and crisis replies. The column is unbounded text.
    /// </summary>
    public string? SystemPromptVersion { get; set; }
}
