using Chatbot.Domain.Rules;

namespace Chatbot.Application.Options;

/// <summary>Plain option classes (bound from configuration in the Api project). Secrets come only from user-secrets / environment variables.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public string Issuer { get; set; } = "lumind-api";

    public string Audience { get; set; } = "lumind-client";

    /// <summary>Secret. At least 32 bytes.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    /// <summary>Secret. Must be the SAME on every environment that shares the database (code hashes depend on it).</summary>
    public string CodePepper { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Sliding lifetime of a refresh token.</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Absolute cap for one sign-in (token family), however often it is refreshed.</summary>
    public int RefreshTokenMaxDays { get; set; } = 30;

    public bool CookieSecure { get; set; } = true;

    /// <summary>Code attempts per client IP within <see cref="CodeWindowSeconds"/>.</summary>
    public int CodeAttempts { get; set; } = 5;

    public int CodeWindowSeconds { get; set; } = 30;

    /// <summary>Refresh calls per client IP within <see cref="RefreshWindowSeconds"/>.</summary>
    public int RefreshAttempts { get; set; } = 30;

    public int RefreshWindowSeconds { get; set; } = 60;
}

public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    // Must equal client/src/app/core/legal/versions.ts.
    public string PrivacyNoticeVersion { get; set; } = "0.1-taslak";

    public string ConsentVersion { get; set; } = "0.1-taslak";

    public DocumentVersions ToVersions() => new(PrivacyNoticeVersion, ConsentVersion);
}

public sealed class PrivacyOptions
{
    public const string Section = "Privacy";

    // TODO(volkan): the final rule (delete content, or keep it) depends on legal advice. Deleting is the safe default.
    public bool DeleteContentOnWithdrawal { get; set; } = true;

    /// <summary>Secret. Key for the hash of client IPs in the audit log (separate from the code pepper).</summary>
    public string IpHashKey { get; set; } = string.Empty;
}

public sealed class ChatOptions
{
    public const string Section = "Chat";

    /// <summary>How many of the latest messages go to the responder as context.</summary>
    public int ContextMessages { get; set; } = 20;

    public int MaxMessageLength { get; set; } = 2000;

    /// <summary>Per participant.</summary>
    public int MessagesPerMinute { get; set; } = 20;

    public FakeChatOptions Fake { get; set; } = new();
}

public sealed class FakeChatOptions
{
    /// <summary>Pause before the first word (the client mock used 800-1500 ms). Tests use 0.</summary>
    public int FirstTokenDelayMs { get; set; } = 1000;

    /// <summary>Pause between words (the client mock used 40-80 ms). Tests use 0.</summary>
    public int TokenDelayMs { get; set; } = 60;
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>The schema version this build expects (the highest api/db script number).</summary>
    public int ExpectedSchemaVersion { get; set; } = 1;
}
