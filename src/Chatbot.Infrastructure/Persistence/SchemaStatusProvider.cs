using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Persistence;

/// <summary>
/// Reads the highest version in app_schema_version. Strictly read-only: the application never creates or changes
/// the schema (the scripts in api/db/ do that, applied by hand).
/// </summary>
public sealed class SchemaStatusProvider(AppDbContext db, DatabaseOptions options) : ISchemaStatusProvider
{
    public async Task<SchemaStatus> GetAsync(CancellationToken ct)
    {
        var expected = options.ExpectedSchemaVersion;
        try
        {
            var found = (await db.Database
                .SqlQueryRaw<int?>("SELECT max(version) AS \"Value\" FROM public.app_schema_version")
                .ToListAsync(ct)).SingleOrDefault();

            return found is { } v && v >= expected
                ? new SchemaStatus(true, expected, v, null)
                : new SchemaStatus(false, expected, found, NotReadyMessage(expected, found));
        }
        catch (Exception ex) when (DbErrors.IsUndefinedTable(ex) || ex.InnerException is not null && DbErrors.IsUndefinedTable(ex.InnerException))
        {
            return new SchemaStatus(false, expected, null, NotReadyMessage(expected, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connection problems etc.: say so without any detail (a message could carry host names).
            return new SchemaStatus(false, expected, null, "Database is not reachable.");
        }
    }

    private static string NotReadyMessage(int expected, int? found) =>
        $"Apply the scripts in api/db/: expected schema version {expected}, found {(found?.ToString() ?? "none")}.";
}
