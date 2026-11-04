using System.Diagnostics;
using Chatbot.Application;

namespace Chatbot.Api.Http;

/// <summary>
/// One place for unexpected failures: `500` and a generic ProblemDetails. Expected failures (<see cref="AppException"/>)
/// become their documented status and code. The log gets the exception TYPE only: messages and stack traces of
/// database errors can contain keys and values, which must never reach a log.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: nothing to answer.
        }
        catch (AppException ex) when (!context.Response.HasStarted)
        {
            await Problems.WriteAsync(context, ex);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException) when (!context.Response.HasStarted)
        {
            // An unreadable or oversized body (Kestrel's limit): the same answer as any other invalid input.
            await Problems.WriteAsync(context, StatusCodes.Status422UnprocessableEntity, ProblemCodes.ValidationFailed);
        }
        catch (Exception ex)
        {
            logger.LogError("Unhandled exception of type {ExceptionType}", ex.GetType().FullName);
            if (!context.Response.HasStarted)
            {
                await Problems.WriteAsync(context, StatusCodes.Status500InternalServerError, "unknown");
            }
        }
    }
}

/// <summary>
/// The request log: method, route TEMPLATE, status code and duration. Never the path values, query string, headers,
/// cookies or body (they can hold ids, tokens or message text).
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            logger.LogInformation(
                "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs} ms",
                context.Request.Method,
                route,
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

/// <summary>Request size limits (bytes). Messages are at most Chat:MaxMessageLength characters, so a body this large is never legitimate.</summary>
public static class ApiLimits
{
    public const int MaxRequestBodyBytes = 64 * 1024;
}

/// <summary>
/// Headers every API answer carries, independent of whatever proxy sits in front: nothing personal is cached (`no-store`),
/// no content sniffing, no referrer. Also refuses an oversized body BEFORE reading it (the same 422 as any invalid input).
/// </summary>
public sealed class ApiHardeningMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            if (!headers.ContainsKey("Cache-Control"))
            {
                headers.CacheControl = "no-store";
            }

            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        }, context);

        if (context.Request.ContentLength is { } length && length > ApiLimits.MaxRequestBodyBytes)
        {
            return Problems.WriteAsync(context, StatusCodes.Status422UnprocessableEntity, ProblemCodes.ValidationFailed);
        }

        return next(context);
    }
}
