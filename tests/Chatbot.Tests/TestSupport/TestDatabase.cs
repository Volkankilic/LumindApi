using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>With REQUIRE_DB_TESTS=1 (CI, reports) an unreachable database FAILS the tests instead of skipping them.</summary>
    public static bool Required => Environment.GetEnvironmentVariable("REQUIRE_DB_TESTS") == "1";

    /// <summary>Why the database cannot be used (null when it is reachable and the schema is at the expected version).</summary>
    public static string? Unavailable => SkipReason.Value;

    /// <summary>What the tests skip with: the reason, unless the database is required (then nothing is skipped).</summary>
    public static string? Skip => Required ? null : Unavailable;

    /// <summary>Call at the start of a database test or fixture: fails loudly when the database is required but unusable.</summary>
    public static bool EnsureAvailable()
    {
        if (Unavailable is null)
        {
            return true;
        }

        if (Required)
        {
            throw new InvalidOperationException($"REQUIRE_DB_TESTS=1 but the database cannot be used: {Unavailable}");
        }

        return false;
    }

    public static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(TestEncryption.Shared) // the same interceptor as the application, with the TEST key
            // The test host creates many ApiFactory hosts (each with a provider of its own). Whichever context is built AFTER the twentieth would
            // otherwise fail with this warning turned into an exception, and then every later database test fails with it: order dependent.
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options);

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
