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

    /// <summary>
    /// Failed code attempts, across ALL clients, within <see cref="FailureBudgetWindowMinutes"/> that raise a warning and an
    /// audit row. It NEVER blocks anyone (a lock-out would hurt legitimate participants). 0 = off.
    /// </summary>
    public int FailureBudgetThreshold { get; set; } = 120;

    public int FailureBudgetWindowMinutes { get; set; } = 10;
}

/// <summary>
/// The admin panel (phase 7). Its own signing key and audience: a participant token never works here and the reverse.
/// Section "Admin" (environment variable names: Admin__JwtSigningKey, ...).
/// </summary>
public sealed class AdminAuthOptions
{
    public const string Section = "Admin";

    /// <summary>Secret, at least 32 bytes. SEPARATE from Auth:JwtSigningKey. The same value locally and on Railway.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "lumind-api";

    public string Audience { get; set; } = "lumind-admin";

    /// <summary>Audience of the short "password accepted, TOTP pending" token.</summary>
    public string MfaAudience { get; set; } = "lumind-admin-mfa";

    public int AccessTokenMinutes { get; set; } = 10;

    public int MfaTokenMinutes { get; set; } = 5;

    /// <summary>Idle limit: the sliding lifetime of the refresh token.</summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>Absolute limit of one sign-in, however often it is refreshed.</summary>
    public int AbsoluteSessionHours { get; set; } = 8;

    /// <summary>Password attempts per client IP within <see cref="LoginWindowSeconds"/>.</summary>
    public int LoginAttempts { get; set; } = 5;

    public int LoginWindowSeconds { get; set; } = 300;

    public int TotpAttempts { get; set; } = 5;

    public int TotpWindowSeconds { get; set; } = 300;

    public int RefreshAttempts { get; set; } = 30;

    public int RefreshWindowSeconds { get; set; } = 60;

    /// <summary>Failures in a row after which the account is locked for a growing time.</summary>
    public int LockAfterFailures { get; set; } = 5;

    /// <summary>The longest lock (1, 2, 4, 8 … minutes, capped here).</summary>
    public int LockMaxMinutes { get; set; } = 15;

    public int PasswordMinLength { get; set; } = 14;

    public int Pbkdf2Iterations { get; set; } = 310_000;

    /// <summary>A conversation without an end and without activity for this long counts as abandoned.</summary>
    public int AbandonedAfterHours { get; set; } = 24;

    /// <summary>Gaps between consecutive messages shorter than this count as active time.</summary>
    public int ActiveGapMinutes { get; set; } = 10;

    public int MaxBulkCreate { get; set; } = 100;
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

    /// <summary>
    /// Secret. Data encryption keys by id (Privacy__DataEncryptionKeys__&lt;id&gt; = base64 of 32 bytes). Several can exist so
    /// older values stay readable; new values are written with <see cref="CurrentDataEncryptionKeyId"/>. They live
    /// outside the database (user-secrets / environment) and must be the SAME on every environment sharing the database.
    /// </summary>
    public Dictionary<string, string> DataEncryptionKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The id (in <see cref="DataEncryptionKeys"/>) used for every new write. Letters, digits, '-' and '_' only.</summary>
    public string CurrentDataEncryptionKeyId { get; set; } = string.Empty;

    /// <summary>Retention periods in days. Empty (null) or 0 = that rule is OFF. The values are decided by the project and the ethics board.</summary>
    public RetentionDaysOptions RetentionDays { get; set; } = new();

    public RetentionJobOptions Retention { get; set; } = new();
}

/// <summary>Every rule is off by default (no value). TODO(volkan): the periods are decided with the ethics board.</summary>
public sealed class RetentionDaysOptions
{
    /// <summary>Conversations (and their messages) older than this: counted from the end, or from the last message when never ended.</summary>
    public int? EndedConversations { get; set; }

    /// <summary>Audit rows of unknown actors (failed code attempts).</summary>
    public int? AnonymousAudit { get; set; }

    /// <summary>All other audit rows (participant, system and, later, researcher actions).</summary>
    public int? AuditLogs { get; set; }

    public bool AnyEnabled => EndedConversations is > 0 || AnonymousAudit is > 0 || AuditLogs is > 0;
}

public sealed class RetentionJobOptions
{
    /// <summary>While true the job only logs how many rows it WOULD delete (counts, never content).</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Also clean refresh tokens that expired more than a day ago (they hold only a hash). Off by default.</summary>
    public bool ExpiredRefreshTokens { get; set; }

    public int IntervalHours { get; set; } = 24;

    /// <summary>Rows deleted per statement.</summary>
    public int BatchSize { get; set; } = 500;
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
    public int ExpectedSchemaVersion { get; set; } = 4;
}
