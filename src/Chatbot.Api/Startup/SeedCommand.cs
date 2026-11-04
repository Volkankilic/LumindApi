using System.Text;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Seed;

namespace Chatbot.Api.Startup;

/// <summary>
/// `generate-seed [--out path]`: writes the seed SQL script (hashes only). It does not connect to any database and
/// is refused outside Development; the script is run by hand, never by the application.
/// </summary>
public static class SeedCommand
{
    public static int Run(IHostEnvironment environment, IServiceProvider services, string[] args, TextWriter? error = null, TextWriter? output = null)
    {
        error ??= Console.Error;
        output ??= Console.Out;
        return RunCore(environment, services, args, error, output);
    }

    private static int RunCore(IHostEnvironment environment, IServiceProvider services, string[] args, TextWriter error, TextWriter output)
    {
        if (!environment.IsDevelopment())
        {
            error.WriteLine("generate-seed only runs in the Development environment.");
            return 2;
        }

        var problems = OptionsValidator.Validate(
            services.GetRequiredService<AuthOptions>(),
            services.GetRequiredService<PrivacyOptions>(),
            services.GetRequiredService<DocumentsOptions>(),
            services.GetRequiredService<ChatOptions>(),
            connectionString: null,
            corsOrigins: [],
            requireDatabase: false);
        if (problems.Count > 0)
        {
            error.WriteLine("Cannot generate the seed: " + string.Join(" ", problems));
            return 1;
        }

        var outIndex = Array.IndexOf(args, "--out");
        var path = outIndex >= 0 && outIndex + 1 < args.Length
            ? Path.GetFullPath(args[outIndex + 1])
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "db", "local", "seed.sql"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, services.GetRequiredService<SeedSqlGenerator>().Generate(), new UTF8Encoding(false));

        output.WriteLine($"Wrote {path}");
        output.WriteLine("Synthetic data only. Contains code hashes, no plain codes and no pepper. Run it by hand after 001_initial.sql and verify.sql.");
        return 0;
    }
}
