using Chatbot.Application.Abstractions;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Persistence;

namespace Chatbot.Infrastructure.Security;

/// <summary>Writes audit entries. Only who/what/when: the caller must never pass content. The IP is stored as a keyed hash.</summary>
public sealed class AuditWriter(AppDbContext db, ISecretHasher hasher, TimeProvider clock) : IAuditWriter
{
    public async Task WriteAsync(
        ActorType actorType,
        Guid? actorId,
        string action,
        string? targetType,
        Guid? targetId,
        string? clientIp,
        CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Timestamp = clock.GetUtcNow(),
            IpHash = hasher.HashIp(clientIp),
        });
        await db.SaveChangesAsync(ct);
    }
}
