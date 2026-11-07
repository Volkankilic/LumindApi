namespace Chatbot.Domain.Entities;

/// <summary>Same idea as <see cref="RefreshToken"/>, for admin sessions: only the SHA-256 of the token is stored.</summary>
public class AdminRefreshToken
{
    public Guid Id { get; set; }

    public Guid AdminId { get; set; }

    public required string TokenHash { get; set; }

    /// <summary>All tokens of one sign-in share a family; reuse of a spent token revokes the family. The access token carries it as `sid`.</summary>
    public Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Sliding: this is the idle limit.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The absolute session limit, copied to every rotated token.</summary>
    public DateTimeOffset FamilyExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? ReplacedByTokenId { get; set; }
}
