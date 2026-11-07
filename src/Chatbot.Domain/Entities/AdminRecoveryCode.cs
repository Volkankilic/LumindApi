namespace Chatbot.Domain.Entities;

/// <summary>A single-use recovery code. Only its keyed hash is stored; the code is shown once, in the terminal.</summary>
public class AdminRecoveryCode
{
    public Guid Id { get; set; }

    public Guid AdminId { get; set; }

    public required string CodeHash { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}
