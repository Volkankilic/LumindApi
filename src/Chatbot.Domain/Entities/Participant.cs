namespace Chatbot.Domain.Entities;

/// <summary>
/// A research participant. There is deliberately no name, e-mail, phone or national id.
/// The participant code is never stored: only <see cref="CodeHash"/> (HMAC-SHA256 with the pepper).
/// </summary>
public class Participant
{
    public Guid Id { get; set; }

    public required string CodeHash { get; set; }

    /// <summary>Anonymous label given by the researcher ("seed-1", "itest-…"). Not personal data.</summary>
    public string? Label { get; set; }

    public ParticipantStatus Status { get; set; }

    /// <summary>Chosen after consent. Phase 4 encrypts this column (keep it a plain text column that a value converter can wrap).</summary>
    public string? Nickname { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}
