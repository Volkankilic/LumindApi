using System.Text;

namespace Chatbot.Application.Options;

/// <summary>
/// Startup validation (fail fast). Messages name the SETTING only, never its value.
/// </summary>
public static class OptionsValidator
{
    public const int MinSecretBytes = 32;

    public static IReadOnlyList<string> Validate(
        AuthOptions auth,
        PrivacyOptions privacy,
        DocumentsOptions documents,
        ChatOptions chat,
        string? connectionString,
        IEnumerable<string> corsOrigins,
        bool requireDatabase,
        bool production = false)
    {
        var problems = new List<string>();

        Secret(problems, "Auth:CodePepper", auth.CodePepper);
        if (requireDatabase)
        {
            Secret(problems, "Auth:JwtSigningKey", auth.JwtSigningKey);
            Secret(problems, "Privacy:IpHashKey", privacy.IpHashKey);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                problems.Add("ConnectionStrings:Default is missing.");
            }

            if (string.IsNullOrWhiteSpace(auth.Issuer))
            {
                problems.Add("Auth:Issuer is missing.");
            }

            if (string.IsNullOrWhiteSpace(auth.Audience))
            {
                problems.Add("Auth:Audience is missing.");
            }

            if (auth.AccessTokenMinutes is < 1 or > 60)
            {
                problems.Add("Auth:AccessTokenMinutes must be between 1 and 60.");
            }

            if (auth.RefreshTokenDays < 1 || auth.RefreshTokenMaxDays < auth.RefreshTokenDays)
            {
                problems.Add("Auth:RefreshTokenDays must be at least 1 and not above Auth:RefreshTokenMaxDays.");
            }

            if (auth.CodeAttempts < 1 || auth.CodeWindowSeconds < 1 || auth.RefreshAttempts < 1 || auth.RefreshWindowSeconds < 1)
            {
                problems.Add("Auth:CodeAttempts, Auth:CodeWindowSeconds, Auth:RefreshAttempts and Auth:RefreshWindowSeconds must be positive.");
            }

            if (string.IsNullOrWhiteSpace(documents.PrivacyNoticeVersion) || string.IsNullOrWhiteSpace(documents.ConsentVersion))
            {
                problems.Add("Documents:PrivacyNoticeVersion and Documents:ConsentVersion are required.");
            }

            if (chat.ContextMessages < 1 || chat.MaxMessageLength < 1 || chat.MessagesPerMinute < 1)
            {
                problems.Add("Chat:ContextMessages, Chat:MaxMessageLength and Chat:MessagesPerMinute must be positive.");
            }

            if (production && !auth.CookieSecure)
            {
                // The refresh cookie must never travel over plain http in a deployed environment.
                problems.Add("Auth:CookieSecure must be true in Production.");
            }

            if (corsOrigins.Any(o => o.Contains('*', StringComparison.Ordinal)))
            {
                problems.Add("Cors:AllowedOrigins must not contain wildcards.");
            }

            problems.AddRange(ValidateEncryption(privacy));
            problems.AddRange(ValidateRetention(privacy));

            if (auth.FailureBudgetThreshold < 0 || auth.FailureBudgetWindowMinutes < 1)
            {
                problems.Add("Auth:FailureBudgetThreshold must not be negative and Auth:FailureBudgetWindowMinutes must be positive.");
            }
        }

        return problems;
    }

    public const int KeyBytes = 32;

    /// <summary>Key ids end up in the stored prefix (enc:v1:&lt;id&gt;:...), so they are restricted to a safe alphabet.</summary>
    public static bool IsValidKeyId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Checks the data encryption keys. Messages name the SETTING (and the key id, which is not secret), never a value.</summary>
    public static IReadOnlyList<string> ValidateEncryption(PrivacyOptions privacy)
    {
        var problems = new List<string>();
        if (privacy.DataEncryptionKeys.Count == 0)
        {
            problems.Add("Privacy:DataEncryptionKeys has no key (set Privacy__DataEncryptionKeys__<id> with user-secrets or an environment variable).");
        }

        foreach (var (id, value) in privacy.DataEncryptionKeys)
        {
            if (!IsValidKeyId(id))
            {
                problems.Add("Privacy:DataEncryptionKeys has a key id with characters other than letters, digits, '-' and '_' (at most 32).");
                continue;
            }

            if (!TryDecodeKey(value, out _))
            {
                problems.Add($"Privacy:DataEncryptionKeys:{id} must be base64 of exactly {KeyBytes} bytes.");
            }
        }

        if (!IsValidKeyId(privacy.CurrentDataEncryptionKeyId) || !privacy.DataEncryptionKeys.ContainsKey(privacy.CurrentDataEncryptionKeyId))
        {
            problems.Add("Privacy:CurrentDataEncryptionKeyId is missing or names no key in Privacy:DataEncryptionKeys.");
        }

        return problems;
    }

    public static bool TryDecodeKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(value.Trim());
            return key.Length == KeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> ValidateRetention(PrivacyOptions privacy)
    {
        var problems = new List<string>();
        var days = privacy.RetentionDays;
        if (days.EndedConversations is < 0 || days.AnonymousAudit is < 0 || days.AuditLogs is < 0)
        {
            problems.Add("Privacy:RetentionDays values must not be negative.");
        }

        if (privacy.Retention.IntervalHours < 1 || privacy.Retention.BatchSize < 1)
        {
            problems.Add("Privacy:Retention:IntervalHours and Privacy:Retention:BatchSize must be positive.");
        }

        return problems;
    }

    private static void Secret(List<string> problems, string name, string? value)
    {
        if (Encoding.UTF8.GetByteCount(value ?? string.Empty) < MinSecretBytes)
        {
            problems.Add($"{name} is missing or shorter than {MinSecretBytes} bytes (set it with user-secrets or an environment variable).");
        }
    }
}
