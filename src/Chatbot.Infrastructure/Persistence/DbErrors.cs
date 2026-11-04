using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chatbot.Infrastructure.Persistence;

public static class DbErrors
{
    private const string UniqueViolation = "23505";
    private const string UndefinedTable = "42P01";

    public static bool IsUniqueViolation(Exception ex) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: UniqueViolation } }
        || ex is PostgresException { SqlState: UniqueViolation };

    public static bool IsUndefinedTable(Exception ex) =>
        ex is PostgresException { SqlState: UndefinedTable };
}
