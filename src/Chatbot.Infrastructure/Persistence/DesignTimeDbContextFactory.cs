using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chatbot.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef dbcontext script` to draft api/db/001_initial.sql. It never connects:
/// scripting reads the model, so the placeholder connection string below is not a real server.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=design-time-only;Database=none")
            .Options);
}
