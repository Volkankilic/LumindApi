using System.Text.RegularExpressions;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Admin;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Domain.Rules;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Admin;

/// <summary>
/// Reads the usage statistics of participants from times and counts only. It selects NO content column and NO nickname:
/// every query below projects explicit, harmless columns (a test scans this folder for the forbidden names).
/// </summary>
internal sealed class ParticipantStatsLoader(AppDbContext db, AdminAuthOptions options, TimeProvider clock)
{
    private sealed record MessageTime(Guid ConversationId, Guid ParticipantId, MessageRole Role, DateTimeOffset CreatedAt);

    public async Task<List<(ParticipantStats Stats, List<ConversationStats> Conversations)>> LoadAsync(
        IReadOnlyCollection<Guid> participantIds, bool withConversations, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var maxGap = TimeSpan.FromMinutes(options.ActiveGapMinutes);
        var abandonedAfter = TimeSpan.FromHours(options.AbandonedAfterHours);

        var participants = await db.Participants.AsNoTracking()
            .Where(p => participantIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Label, p.Status, p.CreatedAt, p.LastSeenAt })
            .ToListAsync(ct);

        var conversations = await db.Conversations.AsNoTracking()
            .Where(c => participantIds.Contains(c.ParticipantId))
            .Select(c => new { c.Id, c.ParticipantId, c.StartedAt, c.EndedAt })
            .ToListAsync(ct);

        var messages = await (from m in db.Messages.AsNoTracking()
                              join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                              where participantIds.Contains(c.ParticipantId)
                              select new MessageTime(m.ConversationId, c.ParticipantId, m.Role, m.CreatedAt)).ToListAsync(ct);

        var logins = await db.AuditLogs.AsNoTracking()
            .Where(a => a.ActorType == ActorType.Participant && a.Action == AuditActions.AuthCodeSucceeded
                && a.ActorId != null && participantIds.Contains(a.ActorId.Value))
            .Select(a => new { ActorId = a.ActorId!.Value, a.Timestamp })
            .ToListAsync(ct);

        var messagesByConversation = messages.ToLookup(m => m.ConversationId);
        var conversationsByParticipant = conversations.ToLookup(c => c.ParticipantId);
        var loginsByParticipant = logins.ToLookup(l => l.ActorId);

        var result = new List<(ParticipantStats, List<ConversationStats>)>(participants.Count);
        foreach (var p in participants)
        {
            var perConversation = new List<ConversationStats>();
            foreach (var c in conversationsByParticipant[p.Id].OrderByDescending(c => c.StartedAt))
            {
                var times = messagesByConversation[c.Id].Select(m => m.CreatedAt).ToList();
                var last = times.Count > 0 ? times.Max() : (DateTimeOffset?)null;
                var lastActivity = last ?? c.StartedAt;
                perConversation.Add(new ConversationStats(
                    c.Id,
                    c.StartedAt,
                    c.EndedAt,
                    c.EndedAt is { } end ? AdminStatsMath.Minutes(end - c.StartedAt) : null,
                    AdminStatsMath.StateOf(c.EndedAt, lastActivity, now, abandonedAfter),
                    messagesByConversation[c.Id].Count(m => m.Role == MessageRole.User),
                    messagesByConversation[c.Id].Count(m => m.Role == MessageRole.Assistant),
                    AdminStatsMath.Minutes(AdminStatsMath.EstimatedActiveTime(times, maxGap)),
                    last));
            }

            var loginTimes = loginsByParticipant[p.Id].Select(l => l.Timestamp).ToList();
            var activity = new[] { p.LastSeenAt }.Concat(perConversation.Select(c => c.LastActivityAt ?? c.StartedAt).Cast<DateTimeOffset?>()).Where(t => t is not null).Max();
            var stats = new ParticipantStats(
                p.Id,
                p.Label,
                p.Status,
                p.CreatedAt,
                loginTimes.Count > 0 ? loginTimes.Min() : null,
                loginTimes.Count > 0 ? loginTimes.Max() : p.LastSeenAt,
                loginTimes.Count,
                perConversation.Count,
                perConversation.Count(c => c.State == ConversationState.Ongoing),
                perConversation.Count(c => c.State == ConversationState.Completed),
                perConversation.Count(c => c.State == ConversationState.Abandoned),
                perConversation.Sum(c => c.UserMessages),
                perConversation.Sum(c => c.AssistantMessages),
                activity,
                Math.Round(perConversation.Sum(c => c.EstimatedActiveMinutes), 1));
            result.Add((stats, withConversations ? perConversation : []));
        }

        return result;
    }
}

public sealed partial class AdminParticipantService(
    AppDbContext db,
    ISecretHasher hasher,
    IAuditWriter audit,
    AdminAuthOptions options,
    TimeProvider clock) : IAdminParticipantService
{
    // Test data is labelled itest-/seed- and the test cleanup deletes by that label: an admin can never create such a label.
    private static readonly string[] ReservedPrefixes = ["itest-", "seed-"];

    public async Task<IReadOnlyList<IssuedParticipantCode>> CreateAsync(Guid adminId, CreateParticipantsInput input, ClientInfo client, CancellationToken ct)
    {
        List<string> labels;
        if (!string.IsNullOrWhiteSpace(input.Label))
        {
            var label = input.Label.Trim();
            if (!LabelPattern().IsMatch(label) || IsReserved(label))
            {
                throw AppException.ValidationFailed();
            }

            labels = [label];
        }
        else
        {
            var prefix = input.LabelPrefix?.Trim() ?? string.Empty;
            var count = input.Count ?? 0;
            if (!PrefixPattern().IsMatch(prefix) || IsReserved(prefix) || count < 1 || count > options.MaxBulkCreate)
            {
                throw AppException.ValidationFailed();
            }

            // Numbering continues after the highest existing number of this prefix (K-001, K-002, ...).
            var existing = await db.Participants.AsNoTracking()
                .Where(p => p.Label != null && p.Label.StartsWith(prefix))
                .Select(p => p.Label!)
                .ToListAsync(ct);
            var next = existing
                .Select(l => int.TryParse(l[prefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;
            labels = [.. Enumerable.Range(next, count).Select(n => $"{prefix}{n:000}")];
        }

        var taken = await db.Participants.AsNoTracking()
            .Where(p => p.Label != null && labels.Contains(p.Label))
            .AnyAsync(ct);
        if (taken)
        {
            throw AppException.LabelTaken();
        }

        var now = clock.GetUtcNow();
        var issued = new List<IssuedParticipantCode>(labels.Count);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var (code, hash) = NewUniqueCode(hashes);
            var participant = new Participant
            {
                Id = Guid.CreateVersion7(),
                CodeHash = hash,
                Label = label,
                Status = ParticipantStatus.Invited,
                CreatedAt = now,
            };
            db.Participants.Add(participant);
            issued.Add(new IssuedParticipantCode(participant.Id, label, code));
        }

        // A code that already exists in the database (astronomically unlikely, 31^8) fails on the unique index: the whole batch is rolled back.
        await db.SaveChangesAsync(ct);
        foreach (var p in issued)
        {
            await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminParticipantCreated, nameof(Participant), p.Id, client.Ip, ct);
        }

        return issued;
    }

    public async Task<IssuedParticipantCode> ResetCodeAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct)
    {
        var participant = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct) ?? throw AppException.NotFound();
        if (participant.Status is ParticipantStatus.Deleted or ParticipantStatus.Revoked)
        {
            throw AppException.InvalidTransition();
        }

        var (code, hash) = NewUniqueCode([]);
        participant.CodeHash = hash;
        await db.SaveChangesAsync(ct);
        await RevokeSessionsAsync(participantId, ct);
        await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminParticipantCodeReset, nameof(Participant), participantId, client.Ip, ct);
        return new IssuedParticipantCode(participant.Id, participant.Label ?? string.Empty, code);
    }

    public async Task RevokeAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct)
    {
        var participant = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct) ?? throw AppException.NotFound();
        if (participant.Status is not (ParticipantStatus.Invited or ParticipantStatus.Active))
        {
            throw AppException.InvalidTransition();
        }

        participant.Status = ParticipantStatus.Revoked;
        await db.SaveChangesAsync(ct);
        await RevokeSessionsAsync(participantId, ct);
        await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminParticipantRevoked, nameof(Participant), participantId, client.Ip, ct);
    }

    public async Task ReactivateAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct)
    {
        var participant = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct) ?? throw AppException.NotFound();
        if (participant.Status != ParticipantStatus.Revoked)
        {
            throw AppException.InvalidTransition(); // includes Deleted: an erased participant never comes back
        }

        // Someone who never signed in goes back to Invited; everyone else was active before.
        participant.Status = participant.LastSeenAt is null ? ParticipantStatus.Invited : ParticipantStatus.Active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminParticipantReactivated, nameof(Participant), participantId, client.Ip, ct);
    }

    public async Task<PagedResult<ParticipantStats>> ListAsync(ParticipantListQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var filtered = db.Participants.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            filtered = filtered.Where(p => p.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            filtered = filtered.Where(p => p.Label != null && p.Label.ToLower().Contains(term));
        }

        var total = await filtered.CountAsync(ct);
        var loader = new ParticipantStatsLoader(db, options, clock);

        if (query.Sort == ParticipantSort.Messages)
        {
            // Message counts are derived, so sort after computing them (pilot scale: a few hundred participants).
            var all = await loader.LoadAsync(await filtered.Select(p => p.Id).ToListAsync(ct), withConversations: false, ct);
            var sortedAll = query.Descending
                ? all.OrderByDescending(x => x.Stats.UserMessages + x.Stats.AssistantMessages)
                : all.OrderBy(x => x.Stats.UserMessages + x.Stats.AssistantMessages);
            var items = sortedAll.ThenBy(x => x.Stats.Label, StringComparer.Ordinal).Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.Stats).ToList();
            return new PagedResult<ParticipantStats>(items, total, page, pageSize);
        }

        var ordered = (query.Sort, query.Descending) switch
        {
            (ParticipantSort.Created, false) => filtered.OrderBy(p => p.CreatedAt),
            (ParticipantSort.Created, true) => filtered.OrderByDescending(p => p.CreatedAt),
            (ParticipantSort.LastSeen, false) => filtered.OrderBy(p => p.LastSeenAt),
            (ParticipantSort.LastSeen, true) => filtered.OrderByDescending(p => p.LastSeenAt),
            (_, true) => filtered.OrderByDescending(p => p.Label),
            _ => filtered.OrderBy(p => p.Label),
        };
        var ids = await ordered.ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(p => p.Id).ToListAsync(ct);
        var loaded = await loader.LoadAsync(ids, withConversations: false, ct);
        var byId = loaded.ToDictionary(x => x.Stats.Id, x => x.Stats);
        return new PagedResult<ParticipantStats>([.. ids.Where(byId.ContainsKey).Select(id => byId[id])], total, page, pageSize);
    }

    public async Task<ParticipantDetail> GetAsync(Guid adminId, Guid participantId, ClientInfo client, CancellationToken ct)
    {
        var loaded = await new ParticipantStatsLoader(db, options, clock).LoadAsync([participantId], withConversations: true, ct);
        if (loaded.Count == 0)
        {
            throw AppException.NotFound();
        }

        await audit.WriteAsync(ActorType.Researcher, adminId, AuditActions.AdminParticipantViewed, nameof(Participant), participantId, client.Ip, ct);
        return new ParticipantDetail(loaded[0].Stats, loaded[0].Conversations);
    }

    // ------------------------------------------------------------------ helpers

    private (string Code, string Hash) NewUniqueCode(HashSet<string> batch)
    {
        while (true)
        {
            var code = ParticipantCode.Generate();
            var hash = hasher.HashCode(code);
            if (batch.Add(hash))
            {
                return (code, hash);
            }
        }
    }

    /// <summary>Closes the open sessions: after a reset or a revoke the old sign-in must not live on.</summary>
    private async Task RevokeSessionsAsync(Guid participantId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var open = await db.RefreshTokens.Where(t => t.ParticipantId == participantId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in open)
        {
            token.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsReserved(string value) =>
        ReservedPrefixes.Any(r => value.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,15}$")]
    private static partial Regex PrefixPattern();
}
