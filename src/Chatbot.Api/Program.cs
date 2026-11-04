using Chatbot.Api.Http;
using Chatbot.Api.Startup;
using Chatbot.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Railway (and similar hosts) inject the port to bind via the PORT environment variable.
// Without it we fall back to the launchSettings / default development URL.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    // [::] is dual-stack (IPv6 and IPv4): Railway's private network is IPv6 only in older environments and dual-stack in newer ones.
    builder.WebHost.UseUrls($"http://[::]:{port}");
}
else if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    // Same address with and without `--no-launch-profile` (the client's dev proxy points here).
    builder.WebHost.UseUrls("http://localhost:5080");
}

// Structured logging. The request log is our own middleware (method, route template, status, duration); content,
// nicknames, codes, tokens and query strings are never logged. Microsoft.* stays at Warning so the framework's own
// request and SQL logs (which carry paths and query strings) are off.
builder.Host.UseSerilog(
    (context, services, logger) =>
{
    logger
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.With<SensitiveScopeEnricher>() // the request scope of the framework carries the raw path
        .WriteTo.Console();

    // Extra sinks registered in DI (the log leak test captures every event this way).
    foreach (var sink in services.GetServices<ILogEventSink>())
    {
        logger.WriteTo.Sink(sink);
    }
},
    preserveStaticLogger: true);

builder.Services.AddInfrastructure(builder.Environment).AddApiServices();

var app = builder.Build();

// `dotnet run --project src/Chatbot.Api -- generate-seed`: writes api/db/local/seed.sql. It never touches a database.
if (args.Contains("generate-seed"))
{
    return SeedCommand.Run(app.Environment, app.Services, args);
}

// Forwarded headers (the real client IP behind a proxy) are trusted ONLY from proxies named in configuration.
var forwarded = ApiServices.BuildForwardedHeaders(app.Configuration);
if (forwarded is not null)
{
    app.UseForwardedHeaders(forwarded);
}

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseStatusCodePages(context => Problems.WriteAsync(
    context.HttpContext,
    context.HttpContext.Response.StatusCode,
    Problems.CodeFor(context.HttpContext.Response.StatusCode)
        ?? (context.HttpContext.Response.StatusCode < 500 ? "validation-failed" : "unknown")));

app.UseCors(ApiServices.CorsPolicy);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// The OpenAPI document (for generating the client's TypeScript types) exists in Development only.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapDevEndpoints(); // test participants for the end-to-end tests; absent (404) in every other environment
}

app.Run();
return 0;

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
