using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Conversations;

public sealed class ChatStore(AppDbContext db, TimeProvider clock, IAuditWriter audit, ILogger<ChatStore> logger) : IChatStore
{
    public Task<Conversation?> FindOwnedConversationAsync(Guid participantId, Guid conversationId, CancellationToken ct) =>
        db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.ParticipantId == participantId, ct);

    public async Task<(Message Message, bool Existed)> AddUserMessageAsync(
        Guid conversationId,
        Guid clientMessageId,
        string text,
        CancellationToken ct)
    {
        var existing = await FindByClientIdAsync(conversationId, clientMessageId, ct);
        if (existing is not null)
        {
            return (existing, true);
        }

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = text,
            CreatedAt = clock.GetUtcNow(),
            ClientMessageId = clientMessageId,
        };
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            return (message, false);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // A second request with the same client message id won the race: use its message, not a 500.
            db.Entry(message).State = EntityState.Detached;
            var winner = await FindByClientIdAsync(conversationId, clientMessageId, ct);
            return (winner ?? throw new InvalidOperationException("Duplicate client message id without a stored message."), true);
        }
    }

    public async Task<Message?> FindReplyAsync(Message userMessage, CancellationToken ct)
    {
        // The reply is the first assistant message after the user message and before the next user message.
        var nextUserAt = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == userMessage.ConversationId
                && m.Role == MessageRole.User
                && m.CreatedAt > userMessage.CreatedAt)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var replies = db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == userMessage.ConversationId
                && m.Role == MessageRole.Assistant
                && m.CreatedAt >= userMessage.CreatedAt);
        if (nextUserAt is { } limit)
        {
            replies = replies.Where(m => m.CreatedAt < limit);
        }

        return await replies.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).FirstOrDefaultAsync(ct);
    }

    public Task DeleteMessageAsync(Guid messageId, CancellationToken ct) =>
        db.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync(ct);

    public async Task MarkCrisisAsync(Guid participantId, Guid conversationId, Guid messageId, CancellationToken ct)
    {
        await db.Messages.Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsCrisis, true), ct);

        // The flag is what matters: an audit failure must never keep the safe reply (card, 112) from the participant.
        try
        {
            await audit.WriteAsync(ActorType.Participant, participantId, AuditActions.CrisisDetected, nameof(Conversation), conversationId, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Writing the crisis audit entry failed with an exception of type {ExceptionType}", ex.GetType().FullName);
        }
    }

    public async Task<IReadOnlyList<Message>> GetRecentAsync(Guid conversationId, int take, CancellationToken ct)
    {
        var newestFirst = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);
        newestFirst.Reverse();
        return newestFirst;
    }

    public async Task<Guid> AddAssistantMessageAsync(
        Guid conversationId,
        string text,
        MessageCompletion completion,
        bool isCrisis,
        string? systemPromptVersion,
        CancellationToken ct)
    {
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            Role = MessageRole.Assistant,
            Content = text,
            CreatedAt = clock.GetUtcNow(),
            Completion = completion,
            IsCrisis = isCrisis,
            SystemPromptVersion = systemPromptVersion,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        return message.Id;
    }

    private Task<Message?> FindByClientIdAsync(Guid conversationId, Guid clientMessageId, CancellationToken ct) =>
        db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.ClientMessageId == clientMessageId, ct);
}
