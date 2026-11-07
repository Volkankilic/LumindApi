namespace Chatbot.Domain.Entities;

/// <summary>
/// "A team member took care of this crisis flag." Which message, which admin, when. There is deliberately NO free text
/// column: nothing about the content can end up here. One OPEN row per message (a partial unique index); reopening
/// closes the row's validity (<see cref="ReopenedAt"/>) and a later acknowledgement adds a new row.
/// </summary>
public class CrisisAcknowledgement
{
    public Guid Id { get; set; }

    /// <summary>The flagged USER message.</summary>
    public Guid MessageId { get; set; }

    public Guid AdminId { get; set; }

    public DateTimeOffset AcknowledgedAt { get; set; }

    public DateTimeOffset? ReopenedAt { get; set; }

    public Guid? ReopenedByAdminId { get; set; }
}
