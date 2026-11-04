using Chatbot.Application;
using Chatbot.Application.Conversations;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Conversations;

public sealed class ConversationService(AppDbContext db, TimeProvider clock) : IConversationService
{
    private const int StartAttempts = 3;

    public async Task<ConversationDto> StartAsync(Guid participantId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = clock.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Conversations
                    .Where(c => c.ParticipantId == participantId && c.EndedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.EndedAt, now), ct);

                var conversation = new Conversation { Id = Guid.CreateVersion7(), ParticipantId = participantId, StartedAt = now };
                db.Conversations.Add(conversation);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new ConversationDto(conversation.Id, conversation.StartedAt, null);
            }
            catch (Exception ex) when (DbErrors.IsUniqueViolation(ex) && attempt < StartAttempts)
            {
                // Two starts raced: the one-active-conversation index let only one win. Try again.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> ListAsync(Guid participantId, CancellationToken ct) =>
        await db.Conversations.AsNoTracking()
            .Where(c => c.ParticipantId == participantId)
            .OrderByDescending(c => c.StartedAt)
            .Select(c => new ConversationSummaryDto(
                c.Id,
                c.StartedAt,
                c.EndedAt,
                db.Messages.Count(m => m.ConversationId == c.Id)))
            .ToListAsync(ct);

    public async Task<ConversationDetailDto> GetAsync(Guid participantId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await FindOwnedAsync(participantId, conversationId, ct);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);
        return new ConversationDetailDto(
            conversation.Id,
            conversation.StartedAt,
            conversation.EndedAt,
            messages.Select(MessageDto.From).ToList());
    }

    public async Task EndAsync(Guid participantId, Guid conversationId, CancellationToken ct)
    {
        _ = await FindOwnedAsync(participantId, conversationId, ct);
        var now = clock.GetUtcNow();

        // Only the first call writes the end time.
        await db.Conversations
            .Where(c => c.Id == conversationId && c.EndedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EndedAt, now), ct);
    }

    private async Task<Conversation> FindOwnedAsync(Guid participantId, Guid conversationId, CancellationToken ct) =>
        await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId && c.ParticipantId == participantId, ct)
        ?? throw AppException.NotFound();
}
