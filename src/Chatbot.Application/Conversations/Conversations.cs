using Chatbot.Application.Abstractions;
using Chatbot.Domain;
using Chatbot.Domain.Entities;

namespace Chatbot.Application.Conversations;

public sealed record ConversationDto(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public sealed record ConversationSummaryDto(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int MessageCount);

/// <summary>Wire form of a message: role and completion are lower case, flags is ["crisis"] or absent.</summary>
public sealed record MessageDto(
    Guid Id,
    string Role,
    string Text,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string>? Flags,
    string? Completion)
{
    public static MessageDto From(Message m) => new(
        m.Id,
        m.Role == MessageRole.User ? "user" : "assistant",
        m.Content,
        m.CreatedAt,
        m.IsCrisis ? ["crisis"] : null,
        m.Completion?.ToString().ToLowerInvariant());
}

public sealed record ConversationDetailDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<MessageDto> Messages);

/// <summary>Another participant's conversation and a missing one are both <see cref="AppException.NotFound"/>.</summary>
public interface IConversationService
{
    /// <summary>Ends the participant's active conversation (if any) and starts a new one, in one transaction.</summary>
    Task<ConversationDto> StartAsync(Guid participantId, ClientInfo client, CancellationToken ct);

    /// <summary>Newest first. No message content.</summary>
    Task<IReadOnlyList<ConversationSummaryDto>> ListAsync(Guid participantId, ClientInfo client, CancellationToken ct);

    Task<ConversationDetailDto> GetAsync(Guid participantId, Guid conversationId, ClientInfo client, CancellationToken ct);

    /// <summary>Idempotent: the end time is written only the first time.</summary>
    Task EndAsync(Guid participantId, Guid conversationId, ClientInfo client, CancellationToken ct);
}
