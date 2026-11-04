namespace Chatbot.Domain.Entities;

public class Conversation
{
    public Guid Id { get; set; }

    public Guid ParticipantId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public PeplauPhase PeplauPhase { get; set; } = PeplauPhase.Orientation;

    public bool IsActive => EndedAt is null;
}
