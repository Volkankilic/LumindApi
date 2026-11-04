namespace Chatbot.Domain.Entities;

/// <summary>
/// Who did what, never with content: no message text, nickname, code or raw IP.
/// No foreign key on <see cref="ActorId"/>, so entries outlive a deleted participant.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; }

    public ActorType ActorType { get; set; }

    public Guid? ActorId { get; set; }

    public required string Action { get; set; }

    public string? TargetType { get; set; }

    public Guid? TargetId { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Keyed hash of the client IP (Privacy:IpHashKey). Null when unknown.</summary>
    public string? IpHash { get; set; }
}
