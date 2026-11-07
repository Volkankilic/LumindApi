using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Admin;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Admin;

/// <summary>Aggregate usage statistics and the CSV export. Times and counts only: no content column is ever read.</summary>
public sealed class AdminStatsService(AppDbContext db, IAuditWriter audit, AdminAuthOptions options, TimeProvider clock) : IAdminStatsService
{
    private const int DailyDays = 14;
    private const int WeeklyWeeks = 8;

    public async Task<StatsSummary> SummaryAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var byStatus = await db.Participants.AsNoTracking()
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Of(ParticipantStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var neverSignedIn = await db.Participants.AsNoTracking()
            .CountAsync(p => p.LastSeenAt == null && p.Status != ParticipantStatus.Deleted && p.Status != ParticipantStatus.Revoked, ct);

        var conversations = await db.Conversations.AsNoTracking()
            .Select(c => new { c.StartedAt, c.EndedAt })
            .ToListAsync(ct);
        var completed = conversations.Where(c => c.EndedAt != null).ToList();

        var since = AdminStatsMath.PeriodStart(now, weekly: true).AddDays(-7 * (WeeklyWeeks - 1));
        var messages = await (from m in db.Messages.AsNoTracking()
                              join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                              where m.Role == MessageRole.User && m.CreatedAt >= since
                              select new { c.ParticipantId, m.CreatedAt }).ToListAsync(ct);

        return new StatsSummary(
            Of(ParticipantStatus.Invited),
            Of(ParticipantStatus.Active),
            Of(ParticipantStatus.Revoked),
            neverSignedIn,
            conversations.Count,
            completed.Count,
            completed.Count == 0 ? null : Math.Round(completed.Average(c => (c.EndedAt!.Value - c.StartedAt).TotalMinutes), 1),
            Series(conversations.Select(c => c.StartedAt), messages.Select(m => (m.ParticipantId, m.CreatedAt)), now, weekly: false, DailyDays),
            Series(conversations.Select(c => c.StartedAt), messages.Select(m => (m.ParticipantId, m.CreatedAt)), now, weekly: true, WeeklyWeeks));
    }

    public async Task<string> ExportCsvAsync(Guid adminId, ClientInfo client, CancellationToken ct)
    {
        var ids = await db.Participants.AsNoTracking().OrderBy(p => p.Label).ThenBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);
        var loaded = await new ParticipantStatsLoader(db, options, clock).LoadAsync(ids, withConversations: false, ct);
        var byId = loaded.ToDictionary(x => x.Stats.Id, x => x.Stats);
        var csv = AdminStatsMath.Csv(ids.Where(byId.ContainsKey).Select(id => byId[id]));
        await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminStatsExported, null, null, client.Ip, ct);
        return csv;
    }

    private static List<ActivityPoint> Series(
        IEnumerable<DateTimeOffset> conversationStarts,
        IEnumerable<(Guid ParticipantId, DateTimeOffset CreatedAt)> userMessages,
        DateTimeOffset now,
        bool weekly,
        int periods)
    {
        var step = weekly ? 7 : 1;
        var last = AdminStatsMath.PeriodStart(now, weekly);
        var starts = Enumerable.Range(0, periods).Select(i => last.AddDays(-step * (periods - 1 - i))).ToList();
        var startsByPeriod = conversationStarts.GroupBy(t => AdminStatsMath.PeriodStart(t, weekly)).ToDictionary(g => g.Key, g => g.Count());
        var messagesByPeriod = userMessages.GroupBy(m => AdminStatsMath.PeriodStart(m.CreatedAt, weekly)).ToDictionary(g => g.Key, g => g.ToList());
        return
        [
            .. starts.Select(s => new ActivityPoint(
                s,
                startsByPeriod.GetValueOrDefault(s),
                messagesByPeriod.TryGetValue(s, out var list) ? list.Count : 0,
                messagesByPeriod.TryGetValue(s, out var l2) ? l2.Select(m => m.ParticipantId).Distinct().Count() : 0)),
        ];
    }
}

/// <summary>
/// Crisis flags for the panel, grouped by conversation. The list carries the participant LABEL, times and counts: never the
/// message, a phrase or a category (those are not stored with the flag). Acknowledging closes every open flag of the
/// conversation, with one audit entry per message.
/// </summary>
public sealed class AdminCrisisService(AppDbContext db, IAuditWriter audit, TimeProvider clock) : IAdminCrisisService
{
    private sealed record Flag(Guid MessageId, Guid ConversationId, DateTimeOffset CreatedAt);

    public async Task<CrisisOverview> ListAsync(Guid adminId, bool openOnly, ClientInfo client, CancellationToken ct)
    {
        var flags = await FlagsAsync(null, ct);
        var messageIds = flags.Select(f => f.MessageId).ToList();
        var acks = await db.CrisisAcknowledgements.AsNoTracking()
            .Where(a => messageIds.Contains(a.MessageId) && a.ReopenedAt == null)
            .Select(a => new { a.MessageId, a.AdminId, a.AcknowledgedAt })
            .ToListAsync(ct);
        var ackByMessage = acks.ToDictionary(a => a.MessageId);

        var conversationIds = flags.Select(f => f.ConversationId).Distinct().ToList();
        var owners = await (from c in db.Conversations.AsNoTracking()
                            join p in db.Participants.AsNoTracking() on c.ParticipantId equals p.Id
                            where conversationIds.Contains(c.Id)
                            select new { ConversationId = c.Id, p.Label }).ToListAsync(ct);
        var labelByConversation = owners.ToDictionary(o => o.ConversationId, o => o.Label);

        var adminIds = acks.Select(a => a.AdminId).Distinct().ToList();
        var names = await db.AdminUsers.AsNoTracking().Where(a => adminIds.Contains(a.Id)).Select(a => new { a.Id, a.Username }).ToListAsync(ct);
        var nameById = names.ToDictionary(n => n.Id, n => n.Username);

        var groups = new List<CrisisGroup>();
        foreach (var g in flags.GroupBy(f => f.ConversationId))
        {
            var open = g.Count(f => !ackByMessage.ContainsKey(f.MessageId));
            var acked = g.Where(f => ackByMessage.ContainsKey(f.MessageId)).Select(f => ackByMessage[f.MessageId]).OrderByDescending(a => a.AcknowledgedAt).FirstOrDefault();
            groups.Add(new CrisisGroup(
                g.Key,
                labelByConversation.GetValueOrDefault(g.Key),
                g.Min(f => f.CreatedAt),
                g.Max(f => f.CreatedAt),
                g.Count(),
                open,
                open == 0 ? acked?.AcknowledgedAt : null,
                open == 0 && acked is not null ? nameById.GetValueOrDefault(acked.AdminId) : null));
        }

        // New and unacknowledged first, then the most recent flag.
        var ordered = groups.OrderByDescending(x => x.OpenCount > 0).ThenByDescending(x => x.LastFlaggedAt).ToList();
        var openConversations = groups.Count(x => x.OpenCount > 0);
        var openMessages = groups.Sum(x => x.OpenCount);
        if (openOnly)
        {
            ordered = [.. ordered.Where(x => x.OpenCount > 0)];
        }

        await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminCrisisViewed, null, null, client.Ip, ct);
        return new CrisisOverview(openConversations, openMessages, ordered);
    }

    public async Task<int> AcknowledgeAsync(Guid adminId, Guid conversationId, ClientInfo client, CancellationToken ct)
    {
        var flags = await FlagsAsync(conversationId, ct);
        if (flags.Count == 0)
        {
            throw AppException.NotFound();
        }

        var ids = flags.Select(f => f.MessageId).ToList();
        var alreadyOpenAck = await db.CrisisAcknowledgements.AsNoTracking()
            .Where(a => ids.Contains(a.MessageId) && a.ReopenedAt == null)
            .Select(a => a.MessageId)
            .ToListAsync(ct);
        var toClose = flags.Where(f => !alreadyOpenAck.Contains(f.MessageId)).ToList();

        var now = clock.GetUtcNow();
        foreach (var flag in toClose)
        {
            db.CrisisAcknowledgements.Add(new CrisisAcknowledgement
            {
                Id = Guid.CreateVersion7(),
                MessageId = flag.MessageId,
                AdminId = adminId,
                AcknowledgedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        foreach (var flag in toClose)
        {
            await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminCrisisAcknowledged, nameof(Message), flag.MessageId, client.Ip, ct);
        }

        return toClose.Count;
    }

    public async Task<int> ReopenAsync(Guid adminId, Guid conversationId, ClientInfo client, CancellationToken ct)
    {
        var flags = await FlagsAsync(conversationId, ct);
        if (flags.Count == 0)
        {
            throw AppException.NotFound();
        }

        var ids = flags.Select(f => f.MessageId).ToList();
        var open = await db.CrisisAcknowledgements
            .Where(a => ids.Contains(a.MessageId) && a.ReopenedAt == null)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        foreach (var ack in open)
        {
            ack.ReopenedAt = now;
            ack.ReopenedByAdminId = adminId;
        }

        await db.SaveChangesAsync(ct);
        foreach (var ack in open)
        {
            await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminCrisisReopened, nameof(Message), ack.MessageId, client.Ip, ct);
        }

        return open.Count;
    }

    /// <summary>The flagged USER messages (the assistant reply is flagged too, but it is not the participant's words). Id, conversation, time only.</summary>
    private async Task<List<Flag>> FlagsAsync(Guid? conversationId, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.IsCrisis && m.Role == MessageRole.User);
        if (conversationId is { } id)
        {
            query = query.Where(m => m.ConversationId == id);
        }

        return await query.OrderBy(m => m.CreatedAt).Select(m => new Flag(m.Id, m.ConversationId, m.CreatedAt)).ToListAsync(ct);
    }
}
