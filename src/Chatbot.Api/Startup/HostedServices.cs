using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;

namespace Chatbot.Api.Startup;

/// <summary>
/// Fail fast: a missing or too short secret stops the application with a message that names the SETTING, never its value.
/// It runs first, as a hosted service, so a host that cannot start says why.
/// </summary>
public sealed class ConfigurationValidationService(
    IConfiguration configuration,
    AuthOptions auth,
    PrivacyOptions privacy,
    DocumentsOptions documents,
    ChatOptions chat,
    ICrisisDetector crisisDetector,
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Production must never run without the real crisis detector (the no-op flags nothing). The message names the
        // requirement only. (Resolving the detector above also loads and logs the phrase list version at startup.)
        if (environment.IsProduction() && !crisisDetector.IsProductionGrade)
        {
            throw new InvalidOperationException(
                "Invalid configuration: a production-grade (rule based) crisis detector is required in Production; the no-op detector is not allowed.");
        }

        var problems = OptionsValidator.Validate(
            auth,
            privacy,
            documents,
            chat,
            configuration.GetConnectionString("Default"),
            configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [],
            requireDatabase: true,
            production: environment.IsProduction());
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", problems));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Read-only check of the schema version at startup. When the scripts in api/db/ were not applied it logs a clear
/// message and the health endpoint reports "not ready". It never creates or changes anything.
/// </summary>
public sealed class SchemaCheckService(IServiceScopeFactory scopes, ILogger<SchemaCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var status = await scope.ServiceProvider.GetRequiredService<ISchemaStatusProvider>().GetAsync(stoppingToken);
            if (status.Ready)
            {
                logger.LogInformation("Database schema is ready (version {Found}).", status.Found);
            }
            else
            {
                logger.LogWarning("Database schema is not ready. {Message}", status.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}

/// <summary>
/// Runs the retention job (Chatbot.Infrastructure.Retention.RetentionService) once a day. With no retention period
/// configured (the default) it does nothing at all. In dry-run mode (the default once a period is set) it only logs counts.
/// A failure is logged by exception TYPE only and the loop carries on with the next interval.
/// </summary>
public sealed class RetentionHostedService(
    IServiceScopeFactory scopes,
    PrivacyOptions privacy,
    ILogger<RetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!privacy.RetentionDays.AnyEnabled && !privacy.Retention.ExpiredRefreshTokens)
        {
            logger.LogInformation("Retention job is off (no retention period is configured).");
            return;
        }

        logger.LogInformation(
            "Retention job is on ({Mode}), every {Hours} h.",
            privacy.Retention.DryRun ? "dry run" : "deleting",
            privacy.Retention.IntervalHours);

        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(privacy.Retention.IntervalHours));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Chatbot.Infrastructure.Retention.RetentionService>().RunAsync(null, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Retention run failed with an exception of type {ExceptionType}", ex.GetType().FullName);
        }
    }
}
