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

    /// <summary>Reserved for phase 6 (which system prompt version produced the reply).</summary>
    public string? SystemPromptVersion { get; set; }
}
