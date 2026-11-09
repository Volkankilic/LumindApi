using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Chat.Llm;

namespace Chatbot.Api.Startup;

/// <summary>
/// `evaluate-models --scenarios &lt;file.json&gt; [--models a,b] [--out &lt;folder&gt;] [--prompt &lt;file&gt;]` (phase 6a, ADR 012): sends the SYNTHETIC
/// scenarios of a JSON file to every listed model through the same pieces the chat uses (crisis detector, personal data masking, system
/// prompt, context rules, the model client with its timeouts and retries) and writes the replies and the timings to a LOCAL folder.
///
/// It never touches the database (nothing here resolves a DbContext), runs in Development on a developer's machine only, shows the target
/// HOST and asks for an explicit "evet" first (the texts leave the machine), and reads the key only from configuration (user-secrets).
/// Nothing it prints or writes contains the key; the replies are written to the result files only, never to the console or a log.
/// The result folder is `api/eval-out/` (git-ignored) or a folder outside the repository: the command refuses any other place.
/// </summary>
public static class ModelEvaluationCommand
{
    public const string Name = "evaluate-models";

    private const int MaxScenarios = 200;
    private const int MaxTurns = 40;
    private const int MaxTextLength = 4000;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Turkish letters stay readable in the file
    };

    public static bool IsCommand(string[] args) => args.Contains(Name);

    private sealed record Turn(string? Role, string? Text);

    private sealed record Scenario(string? Id, string? Title, List<Turn>? Turns);

    private sealed record ScenarioFile(int Version, bool Synthetic, string[]? Models, List<Scenario>? Scenarios);

    private sealed record ResultRow(
        string ScenarioId,
        string? Title,
        string Model,
        string Status,
        string? Reason,
        long? FirstPartMs,
        long TotalMs,
        int Parts,
        string PromptVersion,
        string? Reply);

    public static async Task<int> RunAsync(
        IHostEnvironment environment,
        IServiceProvider services,
        string[] args,
        IAdminConsole console,
        HttpMessageHandler? handler = null,
        CancellationToken ct = default)
    {
        if (!environment.IsDevelopment())
        {
            console.WriteLine("Bu komut yalnızca Development ortamında ve yerelde çalışır.");
            return 2;
        }

        var scenariosPath = ValueOf(args, "--scenarios");
        if (string.IsNullOrWhiteSpace(scenariosPath) || !File.Exists(scenariosPath))
        {
            console.WriteLine("--scenarios <dosya.json> gerekli ve dosya bulunmalı.");
            return 1;
        }

        // ---- the scenario file (the errors name the scenario, never its text)
        var (file, fileProblem) = ReadScenarios(scenariosPath);
        if (file is null)
        {
            console.WriteLine("Senaryo dosyası geçersiz: " + fileProblem);
            return 1;
        }

        var models = (ValueOf(args, "--models")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? file.Models ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (models.Count == 0 || models.Any(m => !LlmEndpointPolicy.IsValidModelName(m)))
        {
            console.WriteLine("En az bir geçerli model adı gerekli (--models a,b ya da dosyadaki \"models\" listesi).");
            return 1;
        }

        // ---- the settings (a real call needs a base URL on the allow list and the key, whatever Llm:Provider says)
        var llm = services.GetRequiredService<LlmOptions>();
        var problems = OptionsValidator.ValidateEndpoint(llm.WithModel(models[0]), development: true);
        if (problems.Count > 0)
        {
            console.WriteLine("Komut çalıştırılamıyor, ayarlar eksik: " + string.Join(" ", problems));
            return 1;
        }

        ISystemPromptProvider prompt;
        var promptPath = ValueOf(args, "--prompt");
        if (promptPath is null)
        {
            prompt = services.GetRequiredService<ISystemPromptProvider>();
        }
        else if (File.Exists(promptPath))
        {
            prompt = SystemPromptText.FromFile(promptPath);
        }
        else
        {
            console.WriteLine("--prompt dosyası bulunamadı.");
            return 1;
        }

        // ---- where the results go
        var apiRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
        var evalRoot = Path.Combine(apiRoot, "eval-out");
        var requested = ValueOf(args, "--out");
        var outDir = Path.GetFullPath(requested ?? Path.Combine(evalRoot, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)));
        if (!IsAllowedOutput(outDir, evalRoot, FindRepositoryRoot(apiRoot)))
        {
            console.WriteLine("Sonuç klasörü api/eval-out/ altında ya da depo dışında olmalı (sonuçlar repoya girmez).");
            return 1;
        }

        // ---- the confirmation: the texts leave this machine
        var host = new Uri(llm.BaseUrl!.Trim()).Host;
        console.WriteLine($"Hedef model sunucusu: {host}");
        console.WriteLine($"Modeller: {string.Join(", ", models)}");
        console.WriteLine($"Senaryo sayısı: {file.Scenarios!.Count}. Yalnızca SENTETİK içerik gönderilir; sonuçlar yerelde kalır.");
        var answer = console.ReadLine("Devam etmek için tam olarak 'evet' yazın: ");
        if (!string.Equals(answer?.Trim(), "evet", StringComparison.Ordinal))
        {
            console.WriteLine("Onay verilmedi. Hiçbir şey gönderilmedi.");
            return 3;
        }

        Directory.CreateDirectory(outDir);
        var crisis = services.GetRequiredService<ICrisisDetector>();
        var redactor = services.GetRequiredService<IPiiRedactor>();
        var chat = services.GetRequiredService<ChatOptions>();
        var logger = services.GetRequiredService<ILogger<OpenAiCompatibleChatResponder>>();

        var rows = new List<ResultRow>();
        using var http = LlmHttpClient.Create(handler);
        await using (var results = new StreamWriter(Path.Combine(outDir, "results.jsonl"), append: false, new UTF8Encoding(false)) { AutoFlush = true })
        {
            foreach (var model in models)
            {
                var responder = new OpenAiCompatibleChatResponder(http, llm.WithModel(model), prompt, logger, development: true);
                foreach (var scenario in file.Scenarios!)
                {
                    var row = await RunScenarioAsync(scenario, model, responder, prompt, crisis, redactor, chat, ct);
                    rows.Add(row);
                    await results.WriteLineAsync(JsonSerializer.Serialize(row, WriteOptions));
                    console.WriteLine($"  [{model}] {row.ScenarioId}: {row.Status}{(row.Reason is null ? string.Empty : " (" + row.Reason + ")")}, {row.TotalMs} ms");
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), Summary(rows, prompt.Version, host), new UTF8Encoding(false), ct);
        console.WriteLine();
        console.WriteLine($"Sonuçlar yazıldı: {outDir}");
        return 0;
    }

    private static async Task<ResultRow> RunScenarioAsync(
        Scenario scenario,
        string model,
        OpenAiCompatibleChatResponder responder,
        ISystemPromptProvider prompt,
        ICrisisDetector crisis,
        IPiiRedactor redactor,
        ChatOptions chat,
        CancellationToken ct)
    {
        var turns = scenario.Turns!;
        var last = turns[^1].Text!;

        // The pipeline looks at the incoming message first: a crisis never reaches a model (the reply would be the fixed crisis text).
        if (crisis.Assess(last).IsCrisis)
        {
            return new ResultRow(scenario.Id!, scenario.Title, model, "crisis-intercepted", null, null, 0, 0, prompt.Version, null);
        }

        // The context is built exactly like the chat does: earlier crisis messages and the replies to them are left out, texts are masked.
        var messages = new List<Message>();
        var previousWasCrisis = false;
        foreach (var turn in turns)
        {
            var user = string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase);
            var isCrisis = user ? crisis.Assess(turn.Text!).IsCrisis : previousWasCrisis;
            messages.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = Guid.Empty,
                Role = user ? MessageRole.User : MessageRole.Assistant,
                Content = turn.Text!,
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(messages.Count),
                Completion = user ? null : MessageCompletion.Complete,
                IsCrisis = isCrisis,
            });
            previousWasCrisis = user && isCrisis;
        }

        var context = new ConversationContext(ChatRules.BuildModelContext(messages, chat.ContextMessages, redactor), Guid.NewGuid());

        var started = Stopwatch.GetTimestamp();
        long? first = null;
        var reply = new StringBuilder();
        var parts = 0;
        try
        {
            await foreach (var chunk in responder.StreamReplyAsync(context, ct))
            {
                first ??= (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                parts++;
                reply.Append(chunk);
            }

            return new ResultRow(scenario.Id!, scenario.Title, model, "ok", null, first, Elapsed(started), parts, prompt.Version, reply.ToString().Trim());
        }
        catch (ModelUnavailableException ex)
        {
            return new ResultRow(
                scenario.Id!, scenario.Title, model, "unavailable", ex.Failure + (ex.StatusCode is null ? string.Empty : " " + ex.StatusCode), first, Elapsed(started), parts, prompt.Version, parts > 0 ? reply.ToString().Trim() : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ResultRow(scenario.Id!, scenario.Title, model, "error", ex.GetType().Name, first, Elapsed(started), parts, prompt.Version, null);
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // ---------------------------------------------------------------- the files

    private static (ScenarioFile? File, string? Problem) ReadScenarios(string path)
    {
        ScenarioFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(path, Encoding.UTF8), ReadOptions);
        }
        catch (JsonException)
        {
            return (null, "dosya geçerli bir JSON değil.");
        }

        if (file is null || file.Version != 1)
        {
            return (null, "\"version\": 1 gerekli.");
        }

        if (!file.Synthetic)
        {
            return (null, "\"synthetic\": true gerekli: yalnızca uydurma (sentetik) senaryolar kullanılır, gerçek kişilerin metni değil.");
        }

        if (file.Scenarios is not { Count: > 0 and <= MaxScenarios } scenarios)
        {
            return (null, $"\"scenarios\" 1-{MaxScenarios} arası öğe içermeli.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            if (string.IsNullOrWhiteSpace(scenario.Id) || !ids.Add(scenario.Id))
            {
                return (null, $"{i + 1}. senaryonun \"id\" değeri boş ya da tekrar ediyor.");
            }

            if (scenario.Turns is not { Count: > 0 and <= MaxTurns } turns)
            {
                return (null, $"\"{scenario.Id}\" senaryosu 1-{MaxTurns} tur içermeli.");
            }

            foreach (var turn in turns)
            {
                var role = turn.Role?.ToLowerInvariant();
                if (role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(turn.Text) || turn.Text.Length > MaxTextLength)
                {
                    return (null, $"\"{scenario.Id}\" senaryosunda \"role\" user/assistant olmalı ve \"text\" boş olmamalı (en çok {MaxTextLength} karakter).");
                }
            }

            if (!string.Equals(turns[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"\"{scenario.Id}\" senaryosunun son turu \"user\" olmalı.");
            }
        }

        return (file, null);
    }

    private static string Summary(List<ResultRow> rows, string promptVersion, string host)
    {
        var text = new StringBuilder();
        text.AppendLine("# Model değerlendirme özeti");
        text.AppendLine();
        text.AppendLine($"- Sunucu: {host}");
        text.AppendLine($"- Sistem istemi sürümü: {promptVersion}");
        text.AppendLine("- Girdiler SENTETİK senaryolardır. Bu klasör yerelde kalır; repoya, sohbete ya da e-postaya konmaz.");
        text.AppendLine();
        text.AppendLine("| Model | Senaryo | Başarılı | Ulaşılamadı | Kriz (modele gitmedi) | Hata | İlk parça ms (medyan / p95) | Toplam ms (medyan / p95) |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var group in rows.GroupBy(r => r.Model))
        {
            var ok = group.Where(r => r.Status == "ok").ToList();
            text.AppendLine(
                $"| {group.Key} | {group.Count()} | {ok.Count} | {group.Count(r => r.Status == "unavailable")} | {group.Count(r => r.Status == "crisis-intercepted")} | {group.Count(r => r.Status == "error")} | "
                + $"{Spread(ok.Where(r => r.FirstPartMs is not null).Select(r => r.FirstPartMs!.Value))} | {Spread(ok.Select(r => r.TotalMs))} |");
        }

        return text.ToString();
    }

    private static string Spread(IEnumerable<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0
            ? "-"
            : $"{sorted[sorted.Count / 2]} / {sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)]}";
    }

    // ---------------------------------------------------------------- where the results may go

    public static bool IsAllowedOutput(string outDir, string evalRoot, string? repositoryRoot) =>
        IsUnder(outDir, evalRoot) || repositoryRoot is null || !IsUnder(outDir, repositoryRoot);

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
