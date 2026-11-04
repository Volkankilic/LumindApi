using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Retention;

/// <summary>How many rows a run deleted (or, in a dry run, WOULD delete). Counts only: never content.</summary>
public sealed record RetentionResult(
    int Conversations,
    int AnonymousAudit,
    int AuditLogs,
    int RefreshTokens,
    bool DryRun,
    bool SkippedBecauseLocked)
{
    public int Total => Conversations + AnonymousAudit + AuditLogs + RefreshTokens;
}

/// <summary>
/// Narrows a run to rows that belong to tests (labels / audit actions with a test prefix; the run itself may leave no audit row). Production never sets it.
/// The integration tests run against the shared database and must not touch the seed participants.
/// </summary>
public sealed record RetentionScope(string? ParticipantLabelPrefix = null, string? AuditActionPrefix = null, bool WriteAudit = true);

/// <summary>
/// Deletes what the configured retention periods allow, in batches. Every period is OFF unless configured. Only
/// ONE instance runs at a time (a PostgreSQL advisory lock), and a dry run only counts. Nothing but counts is logged.
/// This is the ONLY code that deletes audit rows besides the Development-only test cleanup (ADR 007, islem-kayitlari.md).
/// </summary>
public sealed class RetentionService(
    AppDbContext db,
    PrivacyOptions privacy,
    TimeProvider clock,
    IAuditWriter audit,
    ILogger<RetentionService> logger)
{
    /// <summary>Arbitrary constant: the advisory lock key of this job.</summary>
    public const long LockKey = 0x4C554D52_4554L; // "LUMRET"

    private static readonly TimeSpan TokenGrace = TimeSpan.FromDays(1);

    public bool AnythingEnabled => privacy.RetentionDays.AnyEnabled || privacy.Retention.ExpiredRefreshTokens;

    public async Task<RetentionResult> RunAsync(RetentionScope? scope, bool? dryRun, CancellationToken ct)
    {
        var isDry = dryRun ?? privacy.Retention.DryRun;
        await db.Database.OpenConnectionAsync(ct); // the advisory lock lives as long as this connection
        try
        {
            var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct);
            if (!locked)
            {
                logger.LogInformation("Retention run skipped: another instance holds the lock.");
                return new RetentionResult(0, 0, 0, 0, isDry, SkippedBecauseLocked: true);
            }

            try
            {
                return await RunLockedAsync(scope ?? new RetentionScope(), isDry, ct);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<RetentionResult> RunLockedAsync(RetentionScope scope, bool dryRun, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var days = privacy.RetentionDays;
        var batch = privacy.Retention.BatchSize;

        var conversations = days.EndedConversations is > 0 and var c
            ? await Purge(ConversationsOlderThan(now.AddDays(-c), scope), dryRun, batch, DeleteConversations, ct)
            : 0;
        var anonymous = days.AnonymousAudit is > 0 and var a
            ? await Purge(AuditOlderThan(now.AddDays(-a), anonymous: true, scope), dryRun, batch, DeleteAudit, ct)
            : 0;
        var audits = days.AuditLogs is > 0 and var l
            ? await Purge(AuditOlderThan(now.AddDays(-l), anonymous: false, scope), dryRun, batch, DeleteAudit, ct)
            : 0;
        var tokens = privacy.Retention.ExpiredRefreshTokens
            ? await Purge(TokensExpiredBefore(now - TokenGrace, scope), dryRun, batch, DeleteTokens, ct)
            : 0;

        var result = new RetentionResult(conversations, anonymous, audits, tokens, dryRun, SkippedBecauseLocked: false);
        logger.LogInformation(
            "Retention run {Mode}: conversations {Conversations}, anonymous audit {Anonymous}, audit {Audit}, refresh tokens {Tokens}.",
            dryRun ? "(dry run, nothing deleted)" : "(deleted)",
            result.Conversations,
            result.AnonymousAudit,
            result.AuditLogs,
            result.RefreshTokens);

        if (!dryRun && result.Total > 0 && scope.WriteAudit)
        {
            await audit.WriteAsync(ActorType.System, null, AuditActions.RetentionRun, null, null, null, ct);
        }

        return result;
    }

    // ---- what is old enough

    /// <summary>Counted from the end; a conversation that was never ended counts from its last message (or its start).</summary>
    private IQueryable<Conversation> ConversationsOlderThan(DateTimeOffset cutoff, RetentionScope scope)
    {
        var query = db.Conversations.Where(c =>
            (c.EndedAt ?? db.Messages.Where(m => m.ConversationId == c.Id).Max(m => (DateTimeOffset?)m.CreatedAt) ?? c.StartedAt) < cutoff);
        if (scope.ParticipantLabelPrefix is { } prefix)
        {
            query = query.Where(c => db.Participants.Any(p => p.Id == c.ParticipantId && p.Label != null && p.Label.StartsWith(prefix)));
        }

        return query;
    }

    /// <summary>Anonymous = unknown actor (failed code attempts). The rest (participant, system, later researcher) is the other rule.</summary>
    private IQueryable<AuditLog> AuditOlderThan(DateTimeOffset cutoff, bool anonymous, RetentionScope scope)
    {
        var query = anonymous
            ? db.AuditLogs.Where(a => a.ActorType == ActorType.Anonymous && a.Timestamp < cutoff)
            : db.AuditLogs.Where(a => a.ActorType != ActorType.Anonymous && a.Timestamp < cutoff);
        if (scope.AuditActionPrefix is { } prefix)
        {
            query = query.Where(a => a.Action.StartsWith(prefix));
        }

        return query;
    }

    private IQueryable<RefreshToken> TokensExpiredBefore(DateTimeOffset cutoff, RetentionScope scope)
    {
        var query = db.RefreshTokens.Where(t => t.ExpiresAt < cutoff || t.FamilyExpiresAt < cutoff);
        if (scope.ParticipantLabelPrefix is { } prefix)
        {
            query = query.Where(t => db.Participants.Any(p => p.Id == t.ParticipantId && p.Label != null && p.Label.StartsWith(prefix)));
        }

        return query;
    }

    // ---- deleting, a batch at a time (ExecuteDelete never reads or logs a value)

    private Task<int> DeleteConversations(IQueryable<Conversation> query, int batch, CancellationToken ct) =>
        DeleteBatches(query, (q, ids) => q.Where(c => ids.Contains(c.Id)), c => c.Id, batch, ct); // messages go by cascade

    private Task<int> DeleteAudit(IQueryable<AuditLog> query, int batch, CancellationToken ct) =>
        DeleteBatches(query, (q, ids) => q.Where(a => ids.Contains(a.Id)), a => a.Id, batch, ct);

    private Task<int> DeleteTokens(IQueryable<RefreshToken> query, int batch, CancellationToken ct) =>
        DeleteBatches(query, (q, ids) => q.Where(t => ids.Contains(t.Id)), t => t.Id, batch, ct);

    private async Task<int> DeleteBatches<T>(
        IQueryable<T> query,
        Func<IQueryable<T>, List<Guid>, IQueryable<T>> byIds,
        System.Linq.Expressions.Expression<Func<T, Guid>> id,
        int batch,
        CancellationToken ct)
        where T : class
    {
        var total = 0;
        while (true)
        {
            var ids = await query.OrderBy(id).Select(id).Take(batch).ToListAsync(ct);
            if (ids.Count == 0)
            {
                return total;
            }

            total += await byIds(query, ids).ExecuteDeleteAsync(ct);
        }
    }

    private static async Task<int> Purge<T>(
        IQueryable<T> query,
        bool dryRun,
        int batch,
        Func<IQueryable<T>, int, CancellationToken, Task<int>> delete,
        CancellationToken ct)
        where T : class =>
        dryRun ? await query.CountAsync(ct) : await delete(query, batch, ct);
}
