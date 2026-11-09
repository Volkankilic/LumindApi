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
        bool production = false,
        AdminAuthOptions? admin = null)
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
            if (admin is not null)
            {
                problems.AddRange(ValidateAdmin(admin, auth));
            }

            if (auth.FailureBudgetThreshold < 0 || auth.FailureBudgetWindowMinutes < 1)
            {
                problems.Add("Auth:FailureBudgetThreshold must not be negative and Auth:FailureBudgetWindowMinutes must be positive.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The model settings. With the default provider (<c>Fake</c>) nothing else is checked: no key, no URL, nothing leaves the server.
    /// Messages name the SETTING only, never its value.
    /// </summary>
    public static IReadOnlyList<string> ValidateLlm(LlmOptions llm, bool development)
    {
        if (llm.IsReal)
        {
            return ValidateEndpoint(llm, development);
        }

        return string.Equals(llm.Provider, LlmOptions.FakeProvider, StringComparison.OrdinalIgnoreCase)
            ? []
            : ["Llm:Provider must be Fake or OpenAiCompatible."];
    }

    /// <summary>Everything a real model call needs. The evaluation command uses it too, whatever <c>Llm:Provider</c> says.</summary>
    public static IReadOnlyList<string> ValidateEndpoint(LlmOptions llm, bool development)
    {
        var problems = new List<string>();

        problems.AddRange(LlmEndpointPolicy.Check(llm.BaseUrl, llm.AllowedHosts, development));

        if (string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            problems.Add("Llm:ApiKey is missing (set it with user-secrets or the Llm__ApiKey environment variable).");
        }

        else if (llm.ApiKey.Any(char.IsControl) || llm.ApiKey != llm.ApiKey.Trim())
        {
            // A line break would let the key value start a second header; a stray space is a copy mistake.
            problems.Add("Llm:ApiKey must not contain line breaks, control characters or leading/trailing spaces.");
        }

        if (!llm.UsesApiKeyHeader && !string.Equals(llm.AuthScheme, LlmOptions.BearerAuth, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Llm:AuthScheme must be Bearer or ApiKeyHeader.");
        }

        if (!LlmEndpointPolicy.IsValidHeaderName(llm.AuthHeaderName))
        {
            problems.Add("Llm:AuthHeaderName must be a plain HTTP header name (letters, digits and - _ . only; not Authorization, Content-Type, Host or similar).");
        }

        if (string.IsNullOrWhiteSpace(llm.Model))
        {
            problems.Add("Llm:Model is missing.");
        }
        else if (!LlmEndpointPolicy.IsValidModelName(llm.Model))
        {
            problems.Add("Llm:Model has characters a model name does not use.");
        }

        if (llm.Temperature is < 0 or > 2)
        {
            problems.Add("Llm:Temperature must be between 0 and 2.");
        }

        if (llm.MaxTokens is < 1 or > 8192)
        {
            problems.Add("Llm:MaxTokens must be between 1 and 8192.");
        }

        if (string.IsNullOrWhiteSpace(llm.MaxTokensParameter) || llm.MaxTokensParameter.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            problems.Add("Llm:MaxTokensParameter must be a plain field name.");
        }

        if (llm.FirstTokenTimeoutSeconds <= 0 || llm.IdleTimeoutSeconds <= 0 || llm.TotalTimeoutSeconds <= 0)
        {
            problems.Add("Llm:FirstTokenTimeoutSeconds, Llm:IdleTimeoutSeconds and Llm:TotalTimeoutSeconds must be positive.");
        }

        if (llm.MaxRetries is < 0 or > 5 || llm.RetryBaseDelayMs is < 0 or > 10_000)
        {
            problems.Add("Llm:MaxRetries must be 0-5 and Llm:RetryBaseDelayMs 0-10000.");
        }

        problems.AddRange(LlmExtraBody.Check(llm.ExtraBodyJson, llm.MaxTokensParameter, "Llm:ExtraBodyJson"));

        if (llm.MaxConcurrentRequests is < 1 or > 100)
        {
            problems.Add("Llm:MaxConcurrentRequests must be 1-100.");
        }

        return problems;
    }

    /// <summary>The admin panel settings. Messages name the SETTING only.</summary>
    public static IReadOnlyList<string> ValidateAdmin(AdminAuthOptions admin, AuthOptions auth)
    {
        var problems = new List<string>();
        Secret(problems, "Admin:JwtSigningKey", admin.JwtSigningKey);
        if (!string.IsNullOrEmpty(admin.JwtSigningKey) && string.Equals(admin.JwtSigningKey, auth.JwtSigningKey, StringComparison.Ordinal))
        {
            problems.Add("Admin:JwtSigningKey must differ from Auth:JwtSigningKey.");
        }

        if (string.IsNullOrWhiteSpace(admin.Issuer) || string.IsNullOrWhiteSpace(admin.Audience) || string.IsNullOrWhiteSpace(admin.MfaAudience))
        {
            problems.Add("Admin:Issuer, Admin:Audience and Admin:MfaAudience are required.");
        }
        else if (new[] { admin.Audience, admin.MfaAudience, auth.Audience }.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            problems.Add("Admin:Audience, Admin:MfaAudience and Auth:Audience must all be different.");
        }

        if (admin.AccessTokenMinutes is < 1 or > 30 || admin.MfaTokenMinutes is < 1 or > 15)
        {
            problems.Add("Admin:AccessTokenMinutes must be 1-30 and Admin:MfaTokenMinutes 1-15.");
        }

        if (admin.IdleMinutes < 1 || admin.AbsoluteSessionHours < 1 || admin.IdleMinutes > admin.AbsoluteSessionHours * 60)
        {
            problems.Add("Admin:IdleMinutes and Admin:AbsoluteSessionHours must be positive and the idle time not longer than the absolute time.");
        }

        if (admin.LoginAttempts < 1 || admin.LoginWindowSeconds < 1 || admin.TotpAttempts < 1 || admin.TotpWindowSeconds < 1
            || admin.RefreshAttempts < 1 || admin.RefreshWindowSeconds < 1)
        {
            problems.Add("The Admin:*Attempts and Admin:*WindowSeconds settings must be positive.");
        }

        if (admin.LockAfterFailures < 1 || admin.LockMaxMinutes < 1)
        {
            problems.Add("Admin:LockAfterFailures and Admin:LockMaxMinutes must be positive.");
        }

        if (admin.PasswordMinLength < 8)
        {
            problems.Add("Admin:PasswordMinLength must not be below 8.");
        }

        if (admin.Pbkdf2Iterations < 210_000)
        {
            problems.Add("Admin:Pbkdf2Iterations must not be below 210000.");
        }

        if (admin.AbandonedAfterHours < 1 || admin.ActiveGapMinutes < 1 || admin.MaxBulkCreate is < 1 or > 500)
        {
            problems.Add("Admin:AbandonedAfterHours and Admin:ActiveGapMinutes must be positive and Admin:MaxBulkCreate 1-500.");
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
