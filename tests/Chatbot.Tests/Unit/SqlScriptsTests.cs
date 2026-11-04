using System.Text.RegularExpressions;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Chatbot.Tests.Unit;

/// <summary>
/// Checks the hand-run scripts against the EF model WITHOUT a database (the drift test needs the real one, and the real one
/// only matches after the scripts were run by hand). These keep the script files, verify.sql, the model and the expected
/// schema version in step while the scripts are still waiting to be run.
/// </summary>
public class SqlScriptsTests
{
    private static string Db()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Chatbot.sln not found"), "db");
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(Db(), name));

    private static IEnumerable<string> ModelIndexNames()
    {
        using var db = TestEncryption.InMemoryContext();
        return db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .SelectMany(e => e.GetIndexes())
            .Select(i => i.GetDatabaseName()!)
            .ToList();
    }

    [Fact]
    public void EveryIndexOfTheModel_IsCreatedByAScript_AndChecked_ByVerifySql()
    {
        var scripts = Read("001_initial.sql") + Read("002_retention_indexes.sql");
        var verify = Read("verify.sql");
        foreach (var name in ModelIndexNames())
        {
            Assert.Contains($"INDEX IF NOT EXISTS {name} ", scripts, StringComparison.Ordinal);
            Assert.Contains($"'{name}'", verify, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheNewestScript_RecordsTheSchemaVersionTheApplicationExpects()
    {
        var numbered = Directory.EnumerateFiles(Db(), "00*_*.sql").Select(Path.GetFileName).OrderBy(n => n).ToList();
        var newest = numbered[^1]!;
        var expected = new DatabaseOptions().ExpectedSchemaVersion;
        Assert.StartsWith($"{expected:000}_", newest, StringComparison.Ordinal);
        Assert.Matches($@"INSERT INTO public\.app_schema_version \(version, name\) VALUES \({expected},", Read(newest));
        Assert.Contains($"version = {expected} AND name =", Read("verify.sql"), StringComparison.Ordinal);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Db(), "..", "src", "Chatbot.Api", "appsettings.json")));
        Assert.Equal(expected, doc.RootElement.GetProperty("Database").GetProperty("ExpectedSchemaVersion").GetInt32());
    }

    [Fact]
    public void TheNewScript_IsIdempotent_OneTransaction_AndNeverTouchesData()
    {
        var sql = Read("002_retention_indexes.sql");
        var code = string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.Contains("BEGIN;", code, StringComparison.Ordinal);
        Assert.Contains("COMMIT;", code, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (version) DO NOTHING", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b(DROP|TRUNCATE|DELETE|UPDATE|ALTER|CREATE TABLE)\b", RegexOptions.IgnoreCase), code);
        foreach (Match create in Regex.Matches(code, @"CREATE\s+(UNIQUE\s+)?INDEX\b[^;]*;", RegexOptions.IgnoreCase))
        {
            Assert.Contains("IF NOT EXISTS", create.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheRollback_DropsOnlyTheTwoIndexes_AndTheVersionRecord()
    {
        var sql = Read(Path.Combine("rollback", "002_retention_indexes_down.sql"));
        var code = string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.Contains("DROP INDEX IF EXISTS public.ix_audit_logs_timestamp;", code, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX IF EXISTS public.ix_audit_logs_anonymous_timestamp;", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"DROP\s+TABLE|TRUNCATE", RegexOptions.IgnoreCase), code);
    }

    [Fact]
    public void NoScriptContainsASecretOrAPlainCode()
    {
        foreach (var file in Directory.EnumerateFiles(Db(), "*.sql", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}local{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(new Regex(@"password\s*=|enc:v1:|CodePepper\s*=", RegexOptions.IgnoreCase), text);
            foreach (var former in new[] { "ABCD2345", "BCDF3456", "CDFG4567", "DFGH5678", "FGHJ6789" })
            {
                Assert.DoesNotContain(former, text, StringComparison.Ordinal);
            }
        }
    }
}
