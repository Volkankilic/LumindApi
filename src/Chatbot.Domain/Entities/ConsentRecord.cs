namespace Chatbot.Domain.Entities;

/// <summary>Append-only: never updated or deleted by the application. Withdrawing is a new record.</summary>
public class ConsentRecord
{
    public Guid Id { get; set; }

    public Guid ParticipantId { get; set; }

    public ConsentType Type { get; set; }

    public required string DocumentVersion { get; set; }

    public bool Granted { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}
