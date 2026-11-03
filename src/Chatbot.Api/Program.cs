var builder = WebApplication.CreateBuilder(args);

// Railway (and similar hosts) inject the port to bind via the PORT environment variable.
// Without it we fall back to the launchSettings / default development URL.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
