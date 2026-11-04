using System.Text;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Seed;

namespace Chatbot.Api.Startup;

/// <summary>
/// `generate-seed [--out path]`: writes the seed SQL script (hashes only) and prints the NEW plain codes to the terminal. It does not connect to any database and
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
        // Nicknames and message text are written encrypted, so the seed needs the same data encryption keys as the API.
        problems = [.. problems, .. OptionsValidator.ValidateEncryption(services.GetRequiredService<PrivacyOptions>())];
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
        var seed = services.GetRequiredService<SeedSqlGenerator>().Generate();
        File.WriteAllText(path, seed.Sql, new UTF8Encoding(false));

        output.WriteLine($"Wrote {path}");
        output.WriteLine("Synthetic data only. The file has code hashes (no plain codes, no pepper); nicknames and message text are encrypted.");
        output.WriteLine("Run reset-seed-data.sql, then this file, by hand. Never run this file before the reset.");
        output.WriteLine();
        output.WriteLine("NEW participant codes. They are shown ONLY here (not in the file, not in a log): note them now.");
        foreach (var code in seed.Codes)
        {
            output.WriteLine($"  {code.Label}: {code.Code}");
        }

        return 0;
    }
}
