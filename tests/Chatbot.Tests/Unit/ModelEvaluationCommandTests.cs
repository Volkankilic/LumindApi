using System.Net;
using System.Text;
using System.Text.Json;
using Chatbot.Api.Startup;
using Chatbot.Infrastructure;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Tests.Unit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Chatbot.Tests.Unit;

/// <summary>
/// The `evaluate-models` command (phase 6a): synthetic scenarios in, replies and timings out, into a local folder. No real network
/// (a scripted handler), and the database is poisoned: resolving the DbContext would throw, so a pass proves the command never touches it.
/// </summary>
public class ModelEvaluationCommandTests : IDisposable
{
    private const string Key = "sk-EVAL-KEY-98765432100";
    private const string Reply = "Bunu duymak çok ağır olmalı. Buradayım.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eval-test-" + Guid.NewGuid().ToString("N"));

    public ModelEvaluationCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "repo", "api", "src", "Chatbot.Api"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ApiRoot => Path.Combine(_root, "repo", "api");

    private string OutsideRepo => Path.Combine(_root, "outside", "results");

    private sealed class Env(string environmentName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ScriptedConsole(params string[] answers) : IAdminConsole
    {
        private readonly Queue<string> _answers = new(answers);

        public List<string> Output { get; } = [];

        public void WriteLine(string text = "") => Output.Add(text);

        public string? ReadLine(string prompt)
        {
            Output.Add(prompt);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public string? ReadSecret(string prompt) => throw new InvalidOperationException("the command must never ask for a secret");

        public string All => string.Join("\n", Output);
    }

    private (ServiceProvider Services, Env Environment) Build(string environment = "Development", Action<Dictionary<string, string?>>? tweak = null)
    {
        var settings = TestServices.Secrets();
        settings["Llm:BaseUrl"] = "https://llm.example.test/v1";
        settings["Llm:AllowedHosts:0"] = "llm.example.test";
        settings["Llm:ApiKey"] = Key;
        settings["Llm:RetryBaseDelayMs"] = "1";
        settings["Llm:MaxRetries"] = "1";
        tweak?.Invoke(settings);

        var env = new Env(environment, Path.Combine(ApiRoot, "src", "Chatbot.Api"));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IHostEnvironment>(env);
        services.AddLogging();
        services.AddInfrastructure();
        // Poisoned: the evaluation must never reach the database. Resolving the context fails the run.
        services.AddScoped<AppDbContext>(_ => throw new InvalidOperationException("THE DATABASE WAS TOUCHED"));
        return (services.BuildServiceProvider(), env);
    }

    private string WriteScenarios(object content)
    {
        var path = Path.Combine(_root, "scenarios-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(content), new UTF8Encoding(false));
        return path;
    }

    private static object Scenario(string id, params (string Role, string Text)[] turns) =>
        new { id, title = "Başlık " + id, turns = turns.Select(t => new { role = t.Role, text = t.Text }).ToArray() };

    private string StandardFile() => WriteScenarios(new
    {
        version = 1,
        synthetic = true,
        scenarios = new[]
        {
            Scenario("s01", ("user", "Tanı aldım ve çok korkuyorum")),
            Scenario("s02", ("user", "Bugün hiçbir şey yapasım gelmiyor"), ("assistant", "Anlıyorum."), ("user", "Biraz yoruldum")),
        },
    });

    private static FakeLlmHandler OkHandler() => new((_, _) => Task.FromResult(FakeLlmHandler.Sse(
        FakeLlmHandler.Data("Bunu duymak çok ağır olmalı."), FakeLlmHandler.Data(" Buradayım."), "data: [DONE]")));

    private static Task<int> Run(ServiceProvider services, Env env, ScriptedConsole console, FakeLlmHandler handler, params string[] args) =>
        ModelEvaluationCommand.RunAsync(env, services, ["evaluate-models", .. args], console, handler);

    private string[] ResultLines(string outDir) => File.ReadAllLines(Path.Combine(outDir, "results.jsonl"));

    // ---------------------------------------------------------------- the guards

    [Fact]
    public async Task OutsideDevelopment_NothingIsSent_AndNothingIsWritten()
    {
        var (services, env) = Build("Production");
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(2, code);
        Assert.Equal(0, handler.Count);
        Assert.False(Directory.Exists(OutsideRepo));
    }

    [Fact]
    public async Task AScenarioFileThatIsNotMarkedSynthetic_IsRefused_BeforeAnythingIsAsked()
    {
        var (services, env) = Build();
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");
        var file = WriteScenarios(new { version = 1, synthetic = false, scenarios = new[] { Scenario("s01", ("user", "merhaba")) } });

        var code = await Run(services, env, console, handler, "--scenarios", file, "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(1, code);
        Assert.Contains("synthetic", console.All, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
        Assert.False(Directory.Exists(OutsideRepo));
    }

    [Theory]
    [InlineData("""{"version":2,"synthetic":true,"scenarios":[{"id":"a","turns":[{"role":"user","text":"x"}]}]}""")]
    [InlineData("""{"version":1,"synthetic":true,"scenarios":[]}""")]
    [InlineData("""{"version":1,"synthetic":true,"scenarios":[{"id":"a","turns":[{"role":"assistant","text":"x"}]}]}""")] // the last turn must be the user's
    [InlineData("""{"version":1,"synthetic":true,"scenarios":[{"id":"a","turns":[{"role":"system","text":"x"}]}]}""")]
    [InlineData("""{"version":1,"synthetic":true,"scenarios":[{"id":"a","turns":[{"role":"user","text":" "}]}]}""")]
    [InlineData("""{"version":1,"synthetic":true,"scenarios":[{"id":"a","turns":[{"role":"user","text":"x"}]},{"id":"a","turns":[{"role":"user","text":"y"}]}]}""")] // a repeated id
    [InlineData("not json at all {")]
    public async Task ABadScenarioFile_IsRefused_NamingTheProblemNotTheText(string json)
    {
        var (services, env) = Build();
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");
        var file = Path.Combine(_root, "bad.json");
        File.WriteAllText(file, json);

        var code = await Run(services, env, console, handler, "--scenarios", file, "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(1, code);
        Assert.Contains("Senaryo dosyası geçersiz", console.All, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task WithoutAnExplicitYes_NothingLeavesTheMachine()
    {
        var (services, env) = Build();
        var handler = OkHandler();
        var console = new ScriptedConsole("hayır");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(3, code);
        Assert.Equal(0, handler.Count);
        Assert.False(Directory.Exists(OutsideRepo));
    }

    [Fact]
    public async Task TheTargetHostAndTheModelsAreShownBeforeTheQuestion_NeverTheKey()
    {
        var (services, env) = Build();
        var console = new ScriptedConsole("hayır");

        await Run(services, env, console, OkHandler(), "--scenarios", StandardFile(), "--models", "m1,m2", "--out", OutsideRepo);

        var text = console.All;
        Assert.Contains("llm.example.test", text, StringComparison.Ordinal);
        Assert.Contains("m1, m2", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        var order = console.Output.FindIndex(l => l.Contains("llm.example.test", StringComparison.Ordinal));
        var ask = console.Output.FindIndex(l => l.Contains("'evet'", StringComparison.Ordinal));
        Assert.InRange(order, 0, ask - 1);
    }

    [Fact]
    public async Task AMissingKey_StopsTheCommand_NamingTheSettingOnly()
    {
        var (services, env) = Build(tweak: s => s.Remove("Llm:ApiKey"));
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(1, code);
        Assert.Contains("Llm:ApiKey", console.All, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ABaseUrlOffTheAllowList_StopsTheCommand()
    {
        var (services, env) = Build(tweak: s => s["Llm:BaseUrl"] = "https://abroad.example.test/v1");
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(1, code);
        Assert.Contains("Llm:AllowedHosts", console.All, StringComparison.Ordinal);
        Assert.DoesNotContain("abroad.example.test", console.All, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
    }

    // ---------------------------------------------------------------- where the results may go

    [Fact]
    public async Task AResultFolderInsideTheRepositoryButOutsideEvalOut_IsRefused()
    {
        var (services, env) = Build();
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");
        var inRepo = Path.Combine(_root, "repo", "docs", "results");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", inRepo);

        Assert.Equal(1, code);
        Assert.Contains("eval-out", console.All, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
        Assert.False(Directory.Exists(inRepo));
    }

    [Fact]
    public void TheOutputRule_AllowsEvalOutAndFoldersOutsideTheRepository_Only()
    {
        var repo = Path.Combine(_root, "repo");
        var evalRoot = Path.Combine(repo, "api", "eval-out");

        Assert.True(ModelEvaluationCommand.IsAllowedOutput(Path.Combine(evalRoot, "20260101-120000"), evalRoot, repo));
        Assert.True(ModelEvaluationCommand.IsAllowedOutput(evalRoot, evalRoot, repo));
        Assert.True(ModelEvaluationCommand.IsAllowedOutput(Path.Combine(_root, "elsewhere"), evalRoot, repo));
        Assert.False(ModelEvaluationCommand.IsAllowedOutput(Path.Combine(repo, "docs"), evalRoot, repo));
        Assert.False(ModelEvaluationCommand.IsAllowedOutput(repo, evalRoot, repo));
        Assert.False(ModelEvaluationCommand.IsAllowedOutput(Path.Combine(repo, "api", "eval-out-other"), evalRoot, repo)); // a sibling, not a child
        Assert.False(ModelEvaluationCommand.IsAllowedOutput(Path.Combine(evalRoot, "..", "src"), evalRoot, repo)); // no escaping with ..
    }

    [Fact]
    public async Task WithoutAnOutOption_TheResultsGoUnderApiEvalOut()
    {
        var (services, env) = Build();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, OkHandler(), "--scenarios", StandardFile(), "--models", "m1");

        Assert.Equal(0, code);
        var folder = Assert.Single(Directory.GetDirectories(Path.Combine(ApiRoot, "eval-out")));
        Assert.True(File.Exists(Path.Combine(folder, "results.jsonl")));
        Assert.True(File.Exists(Path.Combine(folder, "summary.md")));
    }

    // ---------------------------------------------------------------- what it does

    [Fact]
    public async Task EveryModelGetsEveryScenario_TheModelNameComesFromTheList_AndTheRowsCarryTheReplyAndTheTimings()
    {
        var (services, env) = Build();
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "model-a, model-b", "--out", OutsideRepo);

        Assert.Equal(0, code);
        Assert.Equal(4, handler.Count);
        var models = handler.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString()).ToList();
        Assert.Equal(["model-a", "model-a", "model-b", "model-b"], models);

        var rows = ResultLines(OutsideRepo).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("ok", r.GetProperty("status").GetString());
            Assert.Equal(Reply, r.GetProperty("reply").GetString());
            Assert.True(r.GetProperty("totalMs").GetInt64() >= 0);
            Assert.True(r.GetProperty("firstPartMs").GetInt64() >= 0);
            Assert.Equal(2, r.GetProperty("parts").GetInt32());
            Assert.Matches("^v1\\+[0-9a-f]{8}$", r.GetProperty("promptVersion").GetString()!);
        });
        Assert.Equal(["s01", "s02", "s01", "s02"], rows.Select(r => r.GetProperty("scenarioId").GetString()!).ToArray());

        var summary = File.ReadAllText(Path.Combine(OutsideRepo, "summary.md"));
        Assert.Contains("model-a", summary, StringComparison.Ordinal);
        Assert.Contains("model-b", summary, StringComparison.Ordinal);
        Assert.Contains("SENTETİK", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelListCanComeFromTheFile_AndTheOptionOverridesIt()
    {
        var (services, env) = Build();
        var file = WriteScenarios(new { version = 1, synthetic = true, models = new[] { "from-file" }, scenarios = new[] { Scenario("s01", ("user", "merhaba")) } });

        var fromFile = OkHandler();
        Assert.Equal(0, await Run(services, env, new ScriptedConsole("evet"), fromFile, "--scenarios", file, "--out", OutsideRepo));
        Assert.Equal("from-file", JsonDocument.Parse(fromFile.Requests[0].Body).RootElement.GetProperty("model").GetString());

        var overridden = OkHandler();
        Assert.Equal(0, await Run(services, env, new ScriptedConsole("evet"), overridden, "--scenarios", file, "--models", "from-option", "--out", OutsideRepo));
        Assert.Equal("from-option", JsonDocument.Parse(overridden.Requests[0].Body).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ACrisisScenario_IsInterceptedLikeInTheChat_AndIsNeverSentToAnyModel()
    {
        var (services, env) = Build();
        var file = WriteScenarios(new
        {
            version = 1,
            synthetic = true,
            scenarios = new[]
            {
                Scenario("normal", ("user", "Bugün biraz yorgunum")),
                Scenario("kriz", ("user", "Bazen ölmek istiyorum")),
            },
        });
        var handler = OkHandler();

        var code = await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", file, "--models", "m1,m2", "--out", OutsideRepo);

        Assert.Equal(0, code);
        Assert.Equal(2, handler.Count); // the normal one, once per model
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("ölmek", StringComparison.Ordinal));

        var rows = ResultLines(OutsideRepo).Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.GetProperty("scenarioId").GetString() == "kriz").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("crisis-intercepted", r.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("reply").ValueKind);
        });
    }

    [Fact]
    public async Task TheContextIsBuiltLikeInTheChat_EarlierCrisisTurnsAreLeftOut_AndPersonalDataIsMasked()
    {
        var (services, env) = Build();
        var file = WriteScenarios(new
        {
            version = 1,
            synthetic = true,
            scenarios = new[]
            {
                Scenario(
                    "s01",
                    ("user", "Kendimi öldürmek istiyorum"),
                    ("assistant", "CRISIS-REPLY-TEXT Güvende olman önemli."),
                    ("user", "Şimdi biraz sakinim, bana ornek@example.com adresinden yazabilirsin")),
            },
        });
        var handler = OkHandler();

        var code = await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", file, "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(0, code);
        var body = handler.Requests[0].Body;
        Assert.DoesNotContain("öldürmek", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CRISIS-REPLY-TEXT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ornek@example.com", body, StringComparison.Ordinal);
        Assert.Contains("[E-POSTA]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderThatCannotAnswer_IsARowNotACrash_WithoutTheResponseBody()
    {
        var (services, env) = Build();
        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Status(HttpStatusCode.ServiceUnavailable, "SECRET-BODY something")));

        var code = await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(0, code);
        var rows = ResultLines(OutsideRepo).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.All(rows, r =>
        {
            Assert.Equal("unavailable", r.GetProperty("status").GetString());
            Assert.Equal("ServerError 503", r.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("reply").ValueKind);
        });
        Assert.DoesNotContain("SECRET-BODY", string.Join("\n", ResultLines(OutsideRepo)) + File.ReadAllText(Path.Combine(OutsideRepo, "summary.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKeyAndTheRepliesAreNeverPrinted_TheKeyIsNeverWritten_AndTheFilesAreUtf8WithoutBom()
    {
        var (services, env) = Build();
        var console = new ScriptedConsole("evet");

        await Run(services, env, console, OkHandler(), "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.DoesNotContain(Key, console.All, StringComparison.Ordinal);
        Assert.DoesNotContain("Buradayım", console.All, StringComparison.Ordinal); // the replies live in the files only
        foreach (var name in new[] { "results.jsonl", "summary.md" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(OutsideRepo, name));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, name + " has a BOM");
            Assert.DoesNotContain(Key, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        Assert.Contains("Buradayım", File.ReadAllText(Path.Combine(OutsideRepo, "results.jsonl")), StringComparison.Ordinal); // readable Turkish, not \u escapes
    }

    [Fact]
    public async Task AnotherPromptFile_CanBeTried_AndItsVersionIsRecorded()
    {
        var (services, env) = Build();
        var promptFile = Path.Combine(_root, "variant.txt");
        File.WriteAllText(promptFile, "<!-- version: deneme -->\n<!-- TODO(volkan): gizli not -->\nKısa bir deneme istemi.", new UTF8Encoding(false));
        var handler = OkHandler();

        var code = await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", StandardFile(), "--models", "m1", "--prompt", promptFile, "--out", OutsideRepo);

        Assert.Equal(0, code);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("Kısa bir deneme istemi.", system);
        var row = JsonDocument.Parse(ResultLines(OutsideRepo)[0]).RootElement;
        Assert.StartsWith("deneme+", row.GetProperty("promptVersion").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommand_IsRoutedByName_AndTheNameIsNotAnAdminCommand()
    {
        Assert.True(ModelEvaluationCommand.IsCommand(["evaluate-models", "--scenarios", "x"]));
        Assert.False(ModelEvaluationCommand.IsCommand(["create-admin", "--username", "x"]));
        Assert.False(AdminCommands.IsAdminCommand(["evaluate-models"]));
    }
}
