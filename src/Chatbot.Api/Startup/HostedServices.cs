using Chatbot.Application.Abstractions;
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
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
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
