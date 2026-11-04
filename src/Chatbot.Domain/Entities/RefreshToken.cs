namespace Chatbot.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid ParticipantId { get; set; }

    /// <summary>SHA-256 of the token. The token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>All tokens that descend from one sign-in share a family; reuse of a spent token revokes the family.</summary>
    public Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Hard cap for the sliding expiry of the whole family (copied to every rotated token).</summary>
    public DateTimeOffset FamilyExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? ReplacedByTokenId { get; set; }
}
