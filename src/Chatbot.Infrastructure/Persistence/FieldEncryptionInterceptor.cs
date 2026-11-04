using System.Runtime.CompilerServices;
using Chatbot.Application.Abstractions;
using Chatbot.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Persistence;

/// <summary>
/// The one place where message content and nicknames are encrypted and decrypted (ADR 007), and where audit rows are
/// protected from change:
/// <list type="bullet">
/// <item>Before a save: Added/Modified values are replaced by their ciphertext.</item>
/// <item>After a save (or a failed one): the tracked entity is put back to PLAIN text, original and current alike, so the same
/// context reads consistent values and nothing is encrypted twice.</item>
/// <item>When an entity is materialized: the stored value is decrypted. A value that is not encrypted fails (and is logged by
/// table, column and row id only), it is never silently taken as plain text.</item>
/// <item>An audit row cannot be modified or deleted through the context.</item>
/// </list>
/// Projections (<c>Select(m =&gt; m.Content)</c>), <c>ExecuteUpdate</c> and raw SQL bypass all of this, so a test checks that
/// the source has no such path for these two fields.
/// </summary>
public sealed class FieldEncryptionInterceptor(IFieldProtector protector, ILogger<FieldEncryptionInterceptor> logger)
    : SaveChangesInterceptor, IMaterializationInterceptor
{
    public const string MessagesTable = "messages";
    public const string ParticipantsTable = "participants";

    private sealed record Pending(EntityEntry Entry, string Property, string Plain);

    private readonly ConditionalWeakTable<DbContext, List<Pending>> _pending = [];

    // ---- save

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Encrypt(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Encrypt(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Restore(eventData.Context, saved: true);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context, saved: true);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Restore(eventData.Context, saved: false);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context, saved: false);
        return Task.CompletedTask;
    }

    private void Encrypt(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (context.ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit log rows cannot be modified or deleted by the application.");
        }

        // A save that failed halfway through a previous call must not leave stale state behind.
        _pending.Remove(context);
        var pending = new List<Pending>();

        foreach (var entry in context.ChangeTracker.Entries<Message>().Where(Writes<Message>(nameof(Message.Content))))
        {
            Protect(entry, nameof(Message.Content), MessagesTable, "content", entry.Entity.Id, pending);
        }

        foreach (var entry in context.ChangeTracker.Entries<Participant>().Where(Writes<Participant>(nameof(Participant.Nickname))))
        {
            Protect(entry, nameof(Participant.Nickname), ParticipantsTable, "nickname", entry.Entity.Id, pending);
        }

        if (pending.Count > 0)
        {
            _pending.Add(context, pending);
        }
    }

    private static Func<EntityEntry<T>, bool> Writes<T>(string property)
        where T : class =>
        entry => entry.State == EntityState.Added
            || (entry.State == EntityState.Modified && entry.Property(property).IsModified);

    private void Protect(EntityEntry entry, string property, string table, string column, Guid rowId, List<Pending> pending)
    {
        var plain = (string?)entry.Property(property).CurrentValue;
        if (plain is null)
        {
            return; // a NULL nickname stays NULL
        }

        if (rowId == Guid.Empty)
        {
            throw new InvalidOperationException($"Field {table}.{column} cannot be encrypted without a row id.");
        }

        entry.Property(property).CurrentValue = protector.Protect(table, column, rowId, plain);
        pending.Add(new Pending(entry, property, plain));
    }

    private void Restore(DbContext? context, bool saved)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        _pending.Remove(context);
        foreach (var item in pending)
        {
            if (item.Entry.State == EntityState.Detached)
            {
                continue;
            }

            var property = item.Entry.Property(item.Property);
            if (saved)
            {
                // Saved: the database now holds the ciphertext. The tracked entity keeps the plain text as BOTH original
                // and current value, so it is not seen as changed (and not encrypted a second time).
                property.OriginalValue = item.Plain;
                property.CurrentValue = item.Plain;
                item.Entry.State = EntityState.Unchanged;
            }
            else
            {
                // Failed: the entity stays Added/Modified, with plain text again so a retry encrypts it afresh.
                property.CurrentValue = item.Plain;
            }
        }
    }

    // ---- read

    public object InitializedInstance(MaterializationInterceptionData materializationData, object instance)
    {
        switch (instance)
        {
            case Message message:
                message.Content = Decrypt(MessagesTable, "content", message.Id, message.Content);
                break;
            case Participant { Nickname: { } nickname } participant:
                participant.Nickname = Decrypt(ParticipantsTable, "nickname", participant.Id, nickname);
                break;
        }

        return instance;
    }

    private string Decrypt(string table, string column, Guid rowId, string stored)
    {
        try
        {
            return protector.Unprotect(table, column, rowId, stored);
        }
        catch (PlaintextFieldException)
        {
            // Never silently taken as plain text. Table, column and row id only: no content.
            logger.LogError("Field {Table}.{Column} of row {RowId} is not encrypted.", table, column, rowId);
            throw;
        }
        catch (FieldProtectionException)
        {
            logger.LogError("Field {Table}.{Column} of row {RowId} could not be decrypted.", table, column, rowId);
            throw;
        }
    }
}
