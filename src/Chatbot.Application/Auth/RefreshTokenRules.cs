using Chatbot.Domain.Entities;

namespace Chatbot.Application.Auth;

public enum RefreshTokenState
{
    Valid,

    /// <summary>Past its sliding expiry or the family's absolute cap.</summary>
    Expired,

    /// <summary>Already rotated or revoked and presented again: someone may hold a stolen copy, so the whole family is revoked.</summary>
    Reused,
}

public static class RefreshTokenRules
{
    public static RefreshTokenState Evaluate(RefreshToken token, DateTimeOffset now)
    {
        if (token.RevokedAt is not null || token.ReplacedByTokenId is not null)
        {
            return RefreshTokenState.Reused;
        }

        return token.ExpiresAt <= now || token.FamilyExpiresAt <= now
            ? RefreshTokenState.Expired
            : RefreshTokenState.Valid;
    }

    /// <summary>The same rule for an admin session.</summary>
    public static RefreshTokenState Evaluate(AdminRefreshToken token, DateTimeOffset now)
    {
        if (token.RevokedAt is not null || token.ReplacedByTokenId is not null)
        {
            return RefreshTokenState.Reused;
        }

        return token.ExpiresAt <= now || token.FamilyExpiresAt <= now
            ? RefreshTokenState.Expired
            : RefreshTokenState.Valid;
    }

    /// <summary>Sliding window in minutes (the admin idle limit), but never beyond the absolute cap of the family.</summary>
    public static DateTimeOffset NextExpiryMinutes(DateTimeOffset now, int slidingMinutes, DateTimeOffset familyExpiresAt)
    {
        var sliding = now.AddMinutes(slidingMinutes);
        return sliding < familyExpiresAt ? sliding : familyExpiresAt;
    }

    /// <summary>Sliding window, but never beyond the absolute cap of the family.</summary>
    public static DateTimeOffset NextExpiry(DateTimeOffset now, int slidingDays, DateTimeOffset familyExpiresAt)
    {
        var sliding = now.AddDays(slidingDays);
        return sliding < familyExpiresAt ? sliding : familyExpiresAt;
    }
}
