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
        bool requireDatabase)
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

            if (corsOrigins.Any(o => o.Contains('*', StringComparison.Ordinal)))
            {
                problems.Add("Cors:AllowedOrigins must not contain wildcards.");
            }
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
