using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Chatbot.Tests.TestSupport;

/// <summary>
/// Finds the shared Railway database for integration tests: ConnectionStrings__Default (environment variable) or the
/// user-secrets of this project (same id as the API). The connection string is never printed or put in a skip reason.
/// </summary>
public static class TestDatabase
{
    private static readonly Lazy<string?> Connection = new(() =>
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(typeof(TestDatabase).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        return config.GetConnectionString("Default");
    });

    private static readonly Lazy<string?> SkipReason = new(ComputeSkipReason);

    public static string ConnectionString =>
        Connection.Value ?? throw new InvalidOperationException("No ConnectionStrings:Default (user-secrets or environment).");

    /// <summary>Null when the database is reachable and the schema is at the expected version; otherwise why the tests are skipped.</summary>
    public static string? Skip => SkipReason.Value;

    public static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    private static string? ComputeSkipReason()
    {
        if (string.IsNullOrWhiteSpace(Connection.Value))
        {
            return "No database connection string (user-secrets ConnectionStrings:Default or environment ConnectionStrings__Default).";
        }

        try
        {
            using var connection = new NpgsqlConnection(Connection.Value);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT max(version) FROM public.app_schema_version", connection);
            var version = command.ExecuteScalar() as int?;
            return version is >= 1 ? null : "Schema is not applied (api/db/001_initial.sql).";
        }
        catch (Exception ex)
        {
            // Type only: a message could contain host names.
            return $"Database is not usable ({ex.GetType().Name}).";
        }
    }
}

/// <summary>An integration test against the real database. Skipped (with the reason) when it is not reachable.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        Skip = TestDatabase.Skip;
    }
}
