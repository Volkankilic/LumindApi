using System.Text.Json;
using Chatbot.Application;

namespace Chatbot.Api.Http;

/// <summary>
/// RFC 7807 ProblemDetails with a `code` extension. Bodies never carry user content or internal detail
/// (no stack trace, no SQL, no message text, no echo of what was sent).
/// </summary>
public static class Problems
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, int status, string code, int? retryAfterSeconds = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        if (retryAfterSeconds is { } seconds)
        {
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = new Dictionary<string, object?>
        {
            ["type"] = "about:blank",
            ["title"] = Title(status),
            ["status"] = status,
            ["code"] = code,
        };
        if (retryAfterSeconds is { } retry)
        {
            body["retryAfterSeconds"] = retry;
        }

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted);
    }

    public static Task WriteAsync(HttpContext context, AppException ex) =>
        WriteAsync(context, ex.StatusCode, ex.Code, ex.RetryAfterSeconds);

    public static string? CodeFor(int status) => status switch
    {
        401 => ProblemCodes.Unauthorized,
        404 => ProblemCodes.NotFound,
        422 => ProblemCodes.ValidationFailed,
        429 => ProblemCodes.RateLimited,
        503 => ProblemCodes.Unavailable,
        _ => null,
    };

    private static string Title(int status) => status switch
    {
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Content",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => status >= 500 ? "Internal Server Error" : "Bad Request",
    };
}
