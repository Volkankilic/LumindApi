namespace Chatbot.Application;

/// <summary>The `code` extension of the ProblemDetails error contract (docs: faz-2-api.md, Adım 5).</summary>
public static class ProblemCodes
{
    public const string Unavailable = "unavailable";
    public const string RateLimited = "rate-limited";
    public const string TooLong = "too-long";
    public const string EmptyMessage = "empty-message";
    public const string ConsentRequired = "consent-required";
    public const string ConversationEnded = "conversation-ended";
    public const string StaleDocumentVersion = "stale-document-version";
    public const string ValidationFailed = "validation-failed";
    public const string InvalidCode = "invalid-code";
    public const string NotFound = "not-found";
    public const string Unauthorized = "unauthorized";
    public const string InvalidCredentials = "invalid-credentials";
    public const string Forbidden = "forbidden";
    public const string InvalidTransition = "invalid-transition";
    public const string LabelTaken = "label-taken";
}

/// <summary>
/// An expected failure with a fixed HTTP meaning. The message is only the code: an exception must
/// never carry user content (message text, nickname, participant code, token) or internal detail.
/// </summary>
public sealed class AppException(string code, int statusCode, int? retryAfterSeconds = null) : Exception(code)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    public static AppException InvalidCode() => new(ProblemCodes.InvalidCode, 401);

    public static AppException Unauthorized() => new(ProblemCodes.Unauthorized, 401);

    public static AppException InvalidCredentials() => new(ProblemCodes.InvalidCredentials, 401);

    public static AppException Forbidden() => new(ProblemCodes.Forbidden, 403);

    public static AppException InvalidTransition() => new(ProblemCodes.InvalidTransition, 409);

    public static AppException LabelTaken() => new(ProblemCodes.LabelTaken, 409);

    public static AppException ConsentRequired() => new(ProblemCodes.ConsentRequired, 403);

    public static AppException NotFound() => new(ProblemCodes.NotFound, 404);

    public static AppException ConversationEnded() => new(ProblemCodes.ConversationEnded, 409);

    public static AppException StaleDocumentVersion() => new(ProblemCodes.StaleDocumentVersion, 409);

    public static AppException TooLong() => new(ProblemCodes.TooLong, 422);

    public static AppException EmptyMessage() => new(ProblemCodes.EmptyMessage, 422);

    public static AppException ValidationFailed() => new(ProblemCodes.ValidationFailed, 422);

    public static AppException RateLimited(int retryAfterSeconds) => new(ProblemCodes.RateLimited, 429, retryAfterSeconds);

    public static AppException Unavailable() => new(ProblemCodes.Unavailable, 503);
}
