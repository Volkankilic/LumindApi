using System.Text;
using Chatbot.Application.Options;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Admin;
using Npgsql;

namespace Chatbot.Api.Startup;

/// <summary>The terminal of the admin commands. The real one hides what is typed for a secret; tests feed lines.</summary>
public interface IAdminConsole
{
    void WriteLine(string text = "");

    string? ReadLine(string prompt);

    /// <summary>Reads a secret without echoing it. Never writes it anywhere.</summary>
    string? ReadSecret(string prompt);
}

public sealed class SystemAdminConsole : IAdminConsole
{
    public void WriteLine(string text = "") => Console.WriteLine(text);

    public string? ReadLine(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }

    public string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>
/// `create-admin | set-admin-password | disable-admin | enable-admin --username &lt;name&gt;` (ADR 009). The password is never an argument:
/// it is typed into a hidden prompt. The TOTP secret and the recovery codes are written to the terminal only. The commands run in the
/// Development environment on a developer's machine, and BEFORE anything happens they show the SERVER NAME of the target database
/// (never the user, password or database name) and ask for an explicit "evet". Because the database is shared, a command run locally
/// changes the Railway database; the data encryption key must therefore be the same in both places.
/// </summary>
public static class AdminCommands
{
    public static readonly string[] Names = ["create-admin", "set-admin-password", "reset-admin-totp", "disable-admin", "enable-admin"];

    public static bool IsAdminCommand(string[] args) => args.Any(a => Names.Contains(a));

    public static async Task<int> RunAsync(
        IHostEnvironment environment,
        IServiceProvider services,
        string[] args,
        IAdminConsole console,
        Func<string?>? targetHost = null,
        CancellationToken ct = default)
    {
        var command = args.First(a => Names.Contains(a));
        if (!environment.IsDevelopment())
        {
            console.WriteLine("Bu komut yalnızca Development ortamında ve yerelde çalışır.");
            return 2;
        }

        var problems = OptionsValidator.Validate(
            services.GetRequiredService<AuthOptions>(),
            services.GetRequiredService<PrivacyOptions>(),
            services.GetRequiredService<DocumentsOptions>(),
            services.GetRequiredService<ChatOptions>(),
            services.GetRequiredService<IConfiguration>().GetConnectionString("Default"),
            [],
            requireDatabase: true);
        problems = [.. problems, .. OptionsValidator.ValidateEncryption(services.GetRequiredService<PrivacyOptions>())];
        if (problems.Count > 0)
        {
            console.WriteLine("Komut çalıştırılamıyor, ayarlar eksik: " + string.Join(" ", problems));
            return 1;
        }

        var username = ValueOf(args, "--username") ?? console.ReadLine("Kullanıcı adı: ");
        if (string.IsNullOrWhiteSpace(username))
        {
            console.WriteLine("Kullanıcı adı gerekli.");
            return 1;
        }

        // The target is shown BEFORE anything is read or written. Server name only: no credentials, no database name.
        var host = (targetHost ?? (() => HostOf(services.GetRequiredService<IConfiguration>().GetConnectionString("Default"))))();
        console.WriteLine($"Hedef veritabanı sunucusu: {host ?? "(okunamadı)"}");
        console.WriteLine("Bu sunucudaki ORTAK veritabanında yönetici hesabı değişecek (yerel ve Railway aynı veritabanını kullanır).");
        var answer = console.ReadLine("Devam etmek için tam olarak 'evet' yazın: ");
        if (!string.Equals(answer?.Trim(), "evet", StringComparison.Ordinal))
        {
            console.WriteLine("Onay verilmedi. Hiçbir şey değişmedi.");
            return 3;
        }

        await using var scope = services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AdminAccountService>();
        return command switch
        {
            "create-admin" => await CreateAsync(accounts, console, username, ct),
            "set-admin-password" => await SetPasswordAsync(accounts, console, username, ct),
            "reset-admin-totp" => await ResetTotpAsync(accounts, console, username, ct),
            "disable-admin" => Report(console, await accounts.SetStatusAsync(username, AdminStatus.Disabled, ct), "Hesap devre dışı bırakıldı; açık oturumları kapandı."),
            _ => Report(console, await accounts.SetStatusAsync(username, AdminStatus.Active, ct), "Hesap yeniden etkinleştirildi."),
        };
    }

    private static async Task<int> CreateAsync(AdminAccountService accounts, IAdminConsole console, string username, CancellationToken ct)
    {
        var password = ReadNewPassword(console);
        if (password is null)
        {
            return 1;
        }

        var (problem, pending) = await accounts.PrepareAsync(username, password, ct);
        if (problem != AdminAccountProblem.None || pending is null)
        {
            console.WriteLine(Describe(problem));
            return 1;
        }

        console.WriteLine();
        console.WriteLine("Kimlik doğrulayıcı uygulamanıza (Google Authenticator, Microsoft Authenticator, Aegis vb.) şunu ekleyin.");
        console.WriteLine("BU BİLGİLER YALNIZCA ŞİMDİ, BU EKRANDA GÖSTERİLİR; dosyaya ya da sohbete yazmayın:");
        console.WriteLine($"  otpauth adresi : {pending.OtpauthUri}");
        console.WriteLine($"  gizli anahtar  : {pending.SecretBase32}");
        console.WriteLine();
        var code = console.ReadLine("Uygulamanın gösterdiği 6 haneli kodu girin: ");
        var stored = await accounts.ConfirmAndStoreAsync(pending, code, ct);
        if (stored != AdminAccountProblem.None)
        {
            console.WriteLine(Describe(stored) + " Hesap OLUŞTURULMADI; komutu yeniden çalıştırın.");
            return 1;
        }

        console.WriteLine();
        console.WriteLine($"Yönetici '{pending.Username}' oluşturuldu.");
        console.WriteLine("KURTARMA KODLARI (tek kullanımlık; telefonunuz kaybolursa girişin tek yolu; parola yöneticisine kaydedin):");
        foreach (var recovery in pending.RecoveryCodes)
        {
            console.WriteLine("  " + recovery);
        }

        console.WriteLine();
        console.WriteLine("Pilot için en az İKİ yönetici hesabı oluşturun (kişiye özel, paylaşılan hesap yok).");
        return 0;
    }

    private static async Task<int> SetPasswordAsync(AdminAccountService accounts, IAdminConsole console, string username, CancellationToken ct)
    {
        var password = ReadNewPassword(console);
        if (password is null)
        {
            return 1;
        }

        var problem = await accounts.SetPasswordAsync(username, password, ct);
        return Report(console, problem, "Parola değiştirildi; bu hesabın tüm oturumları kapandı.");
    }

    /// <summary>
    /// Lost or replaced phone: a new TOTP secret and NEW recovery codes, shown in the terminal only. Nothing changes until the first code of
    /// the new secret is entered; then the old secret and ALL old recovery codes stop working and every session of the account is closed.
    /// </summary>
    private static async Task<int> ResetTotpAsync(AdminAccountService accounts, IAdminConsole console, string username, CancellationToken ct)
    {
        var (problem, pending) = await accounts.PrepareTotpResetAsync(username, ct);
        if (problem != AdminAccountProblem.None || pending is null)
        {
            console.WriteLine(Describe(problem));
            return 1;
        }

        console.WriteLine();
        console.WriteLine($"'{pending.Username}' için YENİ kimlik doğrulayıcı kaydı. Kimlik doğrulayıcı uygulamanıza şunu ekleyin (eski kaydı silin).");
        console.WriteLine("BU BİLGİLER YALNIZCA ŞİMDİ, BU EKRANDA GÖSTERİLİR; dosyaya ya da sohbete yazmayın:");
        console.WriteLine($"  otpauth adresi : {pending.OtpauthUri}");
        console.WriteLine($"  gizli anahtar  : {pending.SecretBase32}");
        console.WriteLine();
        var code = console.ReadLine("Uygulamanın gösterdiği 6 haneli kodu girin: ");
        var result = await accounts.ConfirmTotpResetAsync(pending, code, ct);
        if (result != AdminAccountProblem.None)
        {
            console.WriteLine(Describe(result) + " HİÇBİR ŞEY DEĞİŞMEDİ: eski kimlik doğrulayıcı ve eski kurtarma kodları geçerli kalır. Komutu yeniden çalıştırın.");
            return 1;
        }

        console.WriteLine();
        console.WriteLine("Kimlik doğrulayıcı yenilendi. Eski gizli anahtar, ESKİ KURTARMA KODLARININ HEPSİ geçersiz oldu ve hesabın tüm oturumları kapandı.");
        console.WriteLine("YENİ KURTARMA KODLARI (tek kullanımlık; parola yöneticisine kaydedin):");
        foreach (var recovery in pending.RecoveryCodes)
        {
            console.WriteLine("  " + recovery);
        }

        return 0;
    }

    private static string? ReadNewPassword(IAdminConsole console)
    {
        var first = console.ReadSecret("Parola (en az 8 karakter): ");
        var second = console.ReadSecret("Parola (tekrar): ");
        if (string.IsNullOrEmpty(first) || !string.Equals(first, second, StringComparison.Ordinal))
        {
            console.WriteLine("Parolalar boş ya da birbirinden farklı.");
            return null;
        }

        return first;
    }

    private static int Report(IAdminConsole console, AdminAccountProblem problem, string success)
    {
        if (problem != AdminAccountProblem.None)
        {
            console.WriteLine(Describe(problem));
            return 1;
        }

        console.WriteLine(success);
        return 0;
    }

    private static string Describe(AdminAccountProblem problem) => problem switch
    {
        AdminAccountProblem.BadUsername => "Kullanıcı adı 3-32 karakter olmalı: küçük harfle başlar; küçük harf, rakam, nokta, tire, alt çizgi.",
        AdminAccountProblem.UsernameTaken => "Bu kullanıcı adı zaten var.",
        AdminAccountProblem.WeakPassword => "Parola en az 8 karakter olmalı ve kullanıcı adından farklı olmalı.",
        AdminAccountProblem.WrongCode => "Kod doğrulanamadı.",
        AdminAccountProblem.NotFound => "Böyle bir yönetici yok.",
        _ => string.Empty,
    };

    /// <summary>The server name of a connection string. Nothing else is read from it.</summary>
    public static string? HostOf(string? connectionString)
    {
        try
        {
            return string.IsNullOrWhiteSpace(connectionString) ? null : new NpgsqlConnectionStringBuilder(connectionString).Host;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
