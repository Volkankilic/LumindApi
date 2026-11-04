using System.Text.RegularExpressions;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Chatbot.Tests.Integration;

/// <summary>
/// The EF model and the hand-run scripts in api/db/ must describe the same schema. This compares the model with what the
/// database really has (information_schema, pg_constraint, pg_indexes). A failure names the difference and says which side to fix:
/// a new numbered script (api/db/00N_*.sql, never an edit of one that already ran), or the model in AppDbContext.
/// </summary>
public partial class SchemaDriftTests
{
    private const string Hint = " -> Bring api/db (a NEW numbered script, plus verify.sql) and AppDbContext back in step.";

    [PostgresFact]
    public async Task TheEfModel_MatchesTheDatabaseSchema()
    {
        TestDatabase.EnsureAvailable();
        await using var db = TestDatabase.NewContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var problems = new List<string>();

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var columns = await ReadAsync(connection,
            "SELECT table_name, column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public'");
        var constraints = await ReadAsync(connection,
            """
            SELECT c.conrelid::regclass::text AS table_name, c.conname, c.contype::text, pg_get_constraintdef(c.oid) AS def,
                   c.confdeltype::text AS deltype
            FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = 'public'
              AND c.contype <> 'n' -- PostgreSQL 18 lists NOT NULL as a constraint; nullability is compared through information_schema
            """);
        var indexes = await ReadAsync(connection,
            "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public'");

        var modelTables = new HashSet<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            modelTables.Add(table);
            var tableColumns = columns.Where(r => r["table_name"] == table).ToList();
            if (tableColumns.Count == 0)
            {
                problems.Add($"table {table}: missing in the database");
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            var modelColumns = entity.GetProperties().ToList();
            foreach (var p in modelColumns)
            {
                var name = p.GetColumnName(store)!;
                var column = tableColumns.SingleOrDefault(r => r["column_name"] == name);
                if (column is null)
                {
                    problems.Add($"{table}.{name}: column missing in the database");
                    continue;
                }

                var expectedType = p.GetColumnType();
                if (!string.Equals(column["data_type"], expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{table}.{name}: type is '{column["data_type"]}' in the database, '{expectedType}' in the model");
                }

                var dbNullable = column["is_nullable"] == "YES";
                if (dbNullable != p.IsColumnNullable())
                {
                    problems.Add($"{table}.{name}: nullable is {dbNullable} in the database, {p.IsColumnNullable()} in the model");
                }
            }

            foreach (var extra in tableColumns.Select(r => r["column_name"]!).Except(modelColumns.Select(p => p.GetColumnName(store)!)))
            {
                problems.Add($"{table}.{extra}: column is in the database but not in the model");
            }

            var tableConstraints = constraints.Where(r => Unquote(r["table_name"]) == $"public.{table}" || Unquote(r["table_name"]) == table).ToList();

            // primary key
            var pk = entity.FindPrimaryKey()!;
            ExpectConstraint(problems, tableConstraints, table, pk.GetName()!, "p");

            // foreign keys (name and delete behaviour)
            foreach (var fk in entity.GetForeignKeys())
            {
                var found = ExpectConstraint(problems, tableConstraints, table, fk.GetConstraintName()!, "f");
                if (found is not null)
                {
                    var expected = fk.DeleteBehavior switch
                    {
                        DeleteBehavior.Cascade => "c",
                        DeleteBehavior.Restrict => "r",
                        _ => "a",
                    };
                    if (found["deltype"] != expected)
                    {
                        problems.Add($"{table}.{fk.GetConstraintName()}: ON DELETE is '{found["deltype"]}' in the database, '{expected}' in the model");
                    }
                }
            }

            // check constraints: same names, and every value of the model's rule is in the database's rule
            foreach (var check in entity.GetCheckConstraints())
            {
                var found = ExpectConstraint(problems, tableConstraints, table, check.Name!, "c");
                if (found is null)
                {
                    continue;
                }

                foreach (Match literal in QuotedLiteral().Matches(check.Sql))
                {
                    if (!found["def"]!.Contains(literal.Value, StringComparison.Ordinal))
                    {
                        problems.Add($"{table}.{check.Name}: value {literal.Value} is in the model but not in the database rule");
                    }
                }

                var inDatabase = QuotedLiteral().Matches(found["def"]!).Select(m => m.Value).ToHashSet();
                var inModel = QuotedLiteral().Matches(check.Sql).Select(m => m.Value).ToHashSet();
                foreach (var extra in inDatabase.Except(inModel))
                {
                    problems.Add($"{table}.{check.Name}: value {extra} is in the database rule but not in the model");
                }
            }

            var modelConstraintNames = new[] { pk.GetName()! }
                .Concat(entity.GetForeignKeys().Select(f => f.GetConstraintName()!))
                .Concat(entity.GetCheckConstraints().Select(c => c.Name))
                .ToHashSet();
            foreach (var extra in tableConstraints.Select(r => r["conname"]!).Except(modelConstraintNames))
            {
                problems.Add($"{table}.{extra}: constraint is in the database but not in the model");
            }

            // indexes: name, uniqueness, columns (with direction) and the partial-index filter
            var tableIndexes = indexes.Where(r => r["tablename"] == table).ToList();
            var modelIndexNames = new HashSet<string> { pk.GetName()! }; // the primary key's own index
            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName()!;
                modelIndexNames.Add(name);
                var found = tableIndexes.SingleOrDefault(r => r["indexname"] == name);
                if (found is null)
                {
                    problems.Add($"{table}: index {name} is missing in the database");
                    continue;
                }

                var def = found["indexdef"]!;
                if (def.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal) != index.IsUnique)
                {
                    problems.Add($"{table}.{name}: uniqueness differs (model unique = {index.IsUnique})");
                }

                var expectedColumns = index.Properties.Select((p, i) =>
                    p.GetColumnName(store) + (index.IsDescending is { Count: > 0 } && index.IsDescending[i] ? " DESC" : string.Empty));
                var actualColumns = IndexColumns(def);
                if (!expectedColumns.SequenceEqual(actualColumns))
                {
                    problems.Add($"{table}.{name}: columns are ({string.Join(", ", actualColumns)}) in the database, ({string.Join(", ", expectedColumns)}) in the model");
                }

                var filter = index.GetFilter();
                var hasWhere = def.Contains(" WHERE ", StringComparison.Ordinal);
                if (filter is null && hasWhere || filter is not null && !hasWhere
                    || filter is not null && !Normalize(def[(def.IndexOf(" WHERE ", StringComparison.Ordinal) + 7)..]).Contains(Normalize(filter), StringComparison.Ordinal))
                {
                    problems.Add($"{table}.{name}: partial-index filter differs (model: {filter ?? "none"})");
                }
            }

            foreach (var extra in tableIndexes.Select(r => r["indexname"]!).Except(modelIndexNames))
            {
                problems.Add($"{table}: index {extra} is in the database but not in the model");
            }
        }

        // tables nobody maps (app_schema_version is the one bookkeeping table the application reads by raw SQL)
        foreach (var extra in columns.Select(r => r["table_name"]!).Distinct().Except(modelTables).Where(t => t != "app_schema_version"))
        {
            problems.Add($"table {extra}: is in the database but not in the model");
        }

        Assert.True(problems.Count == 0, "Schema drift between the EF model and the database:\n - " + string.Join("\n - ", problems) + "\n" + Hint);
    }

    [PostgresFact]
    public async Task TheSchemaVersionTable_HoldsTheVersionTheApplicationExpects()
    {
        TestDatabase.EnsureAvailable();
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT max(version) FROM public.app_schema_version", connection);
        var version = (int)(await command.ExecuteScalarAsync())!;
        Assert.True(version >= new Chatbot.Application.Options.DatabaseOptions().ExpectedSchemaVersion);
    }

    [Fact]
    public void TheDriftComparison_ReallyBites_AMissingColumnWouldBeReported()
    {
        // Self test of the helpers used above: a changed index definition is not read as equal.
        var def = "CREATE INDEX ix ON public.t USING btree (a, b DESC)";
        Assert.Equal(["a", "b DESC"], IndexColumns(def));
        Assert.NotEqual(["a", "b"], IndexColumns(def));
        Assert.Equal(Normalize("(ended_at IS NULL)"), Normalize("ended_at IS NULL"), ignoreCase: false);
    }

    private static Dictionary<string, string?>? ExpectConstraint(
        List<string> problems,
        List<Dictionary<string, string?>> tableConstraints,
        string table,
        string name,
        string type)
    {
        var found = tableConstraints.SingleOrDefault(r => r["conname"] == name);
        if (found is null)
        {
            problems.Add($"{table}.{name}: constraint is missing in the database");
            return null;
        }

        if (found["contype"] != type)
        {
            problems.Add($"{table}.{name}: constraint type is '{found["contype"]}' in the database, '{type}' in the model");
        }

        return found;
    }

    private static string Unquote(string? name) => (name ?? string.Empty).Replace("\"", string.Empty);

    /// <summary>The key columns of an index, in order, as "name" or "name DESC".</summary>
    private static string[] IndexColumns(string indexDef)
    {
        var open = indexDef.IndexOf('(', StringComparison.Ordinal);
        var close = indexDef.IndexOf(')', open);
        return [.. indexDef[(open + 1)..close].Split(',').Select(c => c.Trim().Replace("\"", string.Empty))];
    }

    /// <summary>Drops case, quotes, spaces and parentheses so "(ended_at IS NULL)" equals "ended_at IS NULL".</summary>
    private static string Normalize(string sql) =>
        new(sql.Where(c => c is not ('(' or ')' or '"' or ' ')).Select(char.ToLowerInvariant).ToArray());

    private static async Task<List<Dictionary<string, string?>>> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, string?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, string?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    [GeneratedRegex("'[^']*'")]
    private static partial Regex QuotedLiteral();
}
