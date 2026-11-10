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
            Assert.Matches("^v3\\+[0-9a-f]{8}$", r.GetProperty("promptVersion").GetString()!);
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
    public async Task InApiKeyHeaderMode_TheKeyGoesInXApiKeyOnly_AndNothingThatIsPrintedOrWrittenHasIt()
    {
        var (services, env) = Build(tweak: s => s["Llm:AuthScheme"] = "ApiKeyHeader");
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(0, code);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(Key, r.Headers["X-API-Key"]);
            Assert.Null(r.Authorization);
            Assert.False(r.Headers.ContainsKey("Authorization"));
        });
        Assert.Contains("X-API-Key", console.All, StringComparison.Ordinal); // the header NAME is shown, never the value
        Assert.DoesNotContain(Key, console.All, StringComparison.Ordinal);
        foreach (var name in new[] { "results.jsonl", "summary.md" })
        {
            Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(OutsideRepo, name)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InApiKeyHeaderMode_AProviderThatEchoesTheKey_LeavesItOutOfTheRowsAndTheConsole()
    {
        var (services, env) = Build(tweak: s => s["Llm:AuthScheme"] = "ApiKeyHeader");
        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Status(HttpStatusCode.Forbidden, "invalid X-API-Key " + Key)));
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.Equal(0, code);
        var everything = console.All + string.Join("\n", ResultLines(OutsideRepo)) + File.ReadAllText(Path.Combine(OutsideRepo, "summary.md"));
        Assert.DoesNotContain(Key, everything, StringComparison.Ordinal);
        Assert.Contains("Rejected 403", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadHeaderNameOrScheme_StopsTheCommand_BeforeAnythingIsAskedOrSent()
    {
        foreach (var (key, value) in new[] { ("Llm:AuthScheme", "Basic"), ("Llm:AuthHeaderName", "X Bad Name") })
        {
            var (services, env) = Build(tweak: s =>
            {
                s["Llm:AuthScheme"] = "ApiKeyHeader";
                s[key] = value;
            });
            var handler = OkHandler();
            var console = new ScriptedConsole("evet");

            var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

            Assert.Equal(1, code);
            Assert.Equal(0, handler.Count);
            Assert.Contains(key, console.All, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, console.All, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- --extra-body, reasoning and token numbers

    private const string ConfigExtra = """{"from_settings":{"x":1}}""";

    private string WriteExtra(string text, string? name = null)
    {
        var path = Path.Combine(_root, name ?? "extra-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static string Raw(object payload) => "data: " + JsonSerializer.Serialize(payload);

    private static string Thought(string field, string text) =>
        Raw(new { choices = new[] { new { delta = new Dictionary<string, string> { [field] = text } } } });

    /// <summary>A reasoning model: 10 + 11 characters of thinking, a two part answer, and the server's own token count (57).</summary>
    private static FakeLlmHandler ReasoningHandler() => new((_, _) => Task.FromResult(FakeLlmHandler.Sse(new ScriptedStream(
        Thought("reasoning_content", "thinking-A") + "\n\n",
        Thought("reasoning", "thinking-BB") + "\n\n",
        new ScriptedStream.Pause(TimeSpan.FromMilliseconds(300)),
        FakeLlmHandler.Data("Bunu duymak çok ağır olmalı.") + "\n\n",
        FakeLlmHandler.Data(" Buradayım.") + "\n\n",
        Raw(new { choices = Array.Empty<object>(), usage = new { completion_tokens = 57 } }) + "\n\n",
        "data: [DONE]\n\n"))));

    [Fact]
    public async Task TheExtraBodyFile_IsMergedIntoTheRequest_AndReplacesTheSetting()
    {
        var (services, env) = Build(tweak: s => s["Llm:ExtraBodyJson"] = ConfigExtra);
        var handler = OkHandler();
        var file = WriteExtra("""{"chat_template_kwargs":{"enable_thinking":false}}""");

        var code = await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", file, "--out", OutsideRepo);

        Assert.Equal(0, code);
        Assert.All(handler.Requests, r =>
        {
            using var body = JsonDocument.Parse(r.Body);
            Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("from_settings", out _)); // the file REPLACES the setting
            Assert.Equal("m1", body.RootElement.GetProperty("model").GetString());
        });
    }

    [Fact]
    public async Task WithoutTheOption_TheSettingsExtraBodyIsUsed()
    {
        var (services, env) = Build(tweak: s => s["Llm:ExtraBodyJson"] = ConfigExtra);
        var handler = OkHandler();

        await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.All(handler.Requests, r => Assert.Contains("from_settings", r.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyExtraBodyFile_MeansNoExtraFieldsAtAll_EvenWhenTheSettingHasSome()
    {
        var (services, env) = Build(tweak: s => s["Llm:ExtraBodyJson"] = ConfigExtra);
        var handler = OkHandler();

        await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", WriteExtra("  "), "--out", OutsideRepo);

        Assert.All(handler.Requests, r => Assert.DoesNotContain("from_settings", r.Body, StringComparison.Ordinal));
        Assert.All(ResultLines(OutsideRepo), l => Assert.Empty(JsonDocument.Parse(l).RootElement.GetProperty("extraBodyKeys").EnumerateArray()));
    }

    [Fact]
    public async Task EachRunCanTakeItsOwnExtraBodyFile()
    {
        var (services, env) = Build();
        var first = OkHandler();
        var second = OkHandler();

        await Run(services, env, new ScriptedConsole("evet"), first, "--scenarios", StandardFile(), "--models", "model-a", "--extra-body", WriteExtra("""{"chat_template_kwargs":{"enable_thinking":false}}"""), "--out", Path.Combine(OutsideRepo, "a"));
        await Run(services, env, new ScriptedConsole("evet"), second, "--scenarios", StandardFile(), "--models", "model-b", "--extra-body", WriteExtra("""{"reasoning_effort":"low"}"""), "--out", Path.Combine(OutsideRepo, "b"));

        Assert.All(first.Requests, r => Assert.Contains("enable_thinking", r.Body, StringComparison.Ordinal));
        Assert.All(second.Requests, r =>
        {
            Assert.Contains("reasoning_effort", r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("enable_thinking", r.Body, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("""{"model":"SECRET-EXTRA-VALUE"}""")]
    [InlineData("""{"max_tokens":1,"SECRET-EXTRA-KEY":"SECRET-EXTRA-VALUE"}""")]
    [InlineData("[1,2] SECRET-EXTRA-VALUE")]
    [InlineData("{ not json SECRET-EXTRA-VALUE")]
    public async Task ABadExtraBodyFile_StopsTheCommand_BeforeAnythingIsAskedOrSent_AndItsTextIsNotPrinted(string text)
    {
        var (services, env) = Build();
        var handler = OkHandler();
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", WriteExtra(text), "--out", OutsideRepo);

        Assert.Equal(1, code);
        Assert.Equal(0, handler.Count);
        Assert.Contains("--extra-body", console.All, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-EXTRA", console.All, StringComparison.Ordinal);
        Assert.False(Directory.Exists(OutsideRepo));
    }

    [Fact]
    public async Task AMissingOrTooLargeExtraBodyFile_StopsTheCommand()
    {
        var (services, env) = Build();
        var handler = OkHandler();

        var missing = new ScriptedConsole("evet");
        Assert.Equal(1, await Run(services, env, missing, handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", Path.Combine(_root, "yok.json"), "--out", OutsideRepo));
        Assert.Contains("bulunamadı", missing.All, StringComparison.Ordinal);

        var big = new ScriptedConsole("evet");
        Assert.Equal(1, await Run(services, env, big, handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", WriteExtra("{\"p\":\"" + new string('x', 3000) + "\"}"), "--out", OutsideRepo));
        Assert.Contains("çok büyük", big.All, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task TheConsoleNamesTheExtraFields_NeverTheirValues_AndTheFileIsOnlyRead()
    {
        var (services, env) = Build();
        var file = WriteExtra("""{"chat_template_kwargs":{"secret_flag":"SECRET-EXTRA-VALUE"},"top_k":20}""");
        var before = File.ReadAllBytes(file);
        var console = new ScriptedConsole("evet");

        var code = await Run(services, env, console, OkHandler(), "--scenarios", StandardFile(), "--models", "m1", "--extra-body", file, "--out", OutsideRepo);

        Assert.Equal(0, code);
        Assert.Contains("Ek istek alanları (yalnızca adlar): chat_template_kwargs, top_k", console.All, StringComparison.Ordinal);
        var everything = console.All + string.Join("\n", ResultLines(OutsideRepo)) + File.ReadAllText(Path.Combine(OutsideRepo, "summary.md"));
        Assert.DoesNotContain("SECRET-EXTRA-VALUE", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_flag", everything, StringComparison.Ordinal); // not even the nested names: only top level keys are listed
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task TheRows_CarryTokensReasoningLengthAndTheExtraKeys_ButNeverTheReasoningText()
    {
        var (services, env) = Build();
        var handler = ReasoningHandler();
        var console = new ScriptedConsole("evet");
        var file = WriteExtra("""{"chat_template_kwargs":{"enable_thinking":false},"stream_options":{"include_usage":true}}""");

        var code = await Run(services, env, console, handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", file, "--out", OutsideRepo);

        Assert.Equal(0, code);
        var rows = ResultLines(OutsideRepo).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("ok", r.GetProperty("status").GetString());
            Assert.Equal(Reply, r.GetProperty("reply").GetString()); // the answer only: no reasoning in it
            Assert.Equal(57, r.GetProperty("completionTokens").GetInt32());
            Assert.Equal(21, r.GetProperty("reasoningChars").GetInt64()); // 10 + 11 characters, counted
            Assert.Equal(["chat_template_kwargs", "stream_options"], r.GetProperty("extraBodyKeys").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.Equal(2, r.GetProperty("parts").GetInt32()); // the reasoning events are not parts
            Assert.InRange(r.GetProperty("firstPartMs").GetInt64(), 250, 10_000); // measured on the content, after the pause
        });

        var everything = console.All + string.Join("\n", ResultLines(OutsideRepo)) + File.ReadAllText(Path.Combine(OutsideRepo, "summary.md"));
        Assert.DoesNotContain("thinking-A", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("thinking-BB", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummary_HasTheMedianTokensAndTheMedianReasoningChars_PerModel()
    {
        var (services, env) = Build();

        await Run(services, env, new ScriptedConsole("evet"), ReasoningHandler(), "--scenarios", StandardFile(), "--models", "m1,m2", "--out", OutsideRepo);

        var summary = File.ReadAllText(Path.Combine(OutsideRepo, "summary.md"));
        Assert.Contains("Jeton (medyan)", summary, StringComparison.Ordinal);
        Assert.Contains("reasoningChars (medyan)", summary, StringComparison.Ordinal);
        var lines = summary.Split('\n').Where(l => l.StartsWith("| m", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l =>
        {
            var cells = l.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("57", cells[^2]);
            Assert.Equal("21", cells[^1]);
        });
    }

    [Fact]
    public async Task WhenTheServerSendsNoUsage_TokensAreNull_AndTheSummaryShowsADash()
    {
        var (services, env) = Build();

        await Run(services, env, new ScriptedConsole("evet"), OkHandler(), "--scenarios", StandardFile(), "--models", "m1", "--out", OutsideRepo);

        Assert.All(ResultLines(OutsideRepo), l =>
        {
            var row = JsonDocument.Parse(l).RootElement;
            Assert.Equal(JsonValueKind.Null, row.GetProperty("completionTokens").ValueKind);
            Assert.Equal(0, row.GetProperty("reasoningChars").GetInt64());
        });
        var cells = File.ReadAllText(Path.Combine(OutsideRepo, "summary.md")).Split('\n').First(l => l.StartsWith("| m1", StringComparison.Ordinal))
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("-", cells[^2]);
        Assert.Equal("0", cells[^1]);
    }

    [Fact]
    public async Task ACrisisRow_HasNoNumbers_ButStillNamesTheExtraKeysThatWouldHaveBeenSent()
    {
        var (services, env) = Build(tweak: s => s["Llm:ExtraBodyJson"] = ConfigExtra);
        var file = WriteScenarios(new { version = 1, synthetic = true, scenarios = new[] { Scenario("kriz", ("user", "Bazen ölmek istiyorum")) } });

        await Run(services, env, new ScriptedConsole("evet"), OkHandler(), "--scenarios", file, "--models", "m1", "--out", OutsideRepo);

        var row = JsonDocument.Parse(ResultLines(OutsideRepo).Single()).RootElement;
        Assert.Equal("crisis-intercepted", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("completionTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reasoningChars").ValueKind);
        Assert.Equal(["from_settings"], row.GetProperty("extraBodyKeys").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task AnExtraBodyThatTheServerRejects_IsAnUnavailableRowWithTheStatus_NotACrash()
    {
        // the real EVREN behaviour: unknown fields answer 400
        var (services, env) = Build();
        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Status(HttpStatusCode.BadRequest, "unknown field SECRET-EXTRA-VALUE")));

        var code = await Run(services, env, new ScriptedConsole("evet"), handler, "--scenarios", StandardFile(), "--models", "m1", "--extra-body", WriteExtra("""{"reasoning_effort":"low"}"""), "--out", OutsideRepo);

        Assert.Equal(0, code);
        Assert.Equal(2, handler.Count); // one attempt per scenario: a 400 is not repeated
        Assert.All(ResultLines(OutsideRepo), l =>
        {
            var row = JsonDocument.Parse(l).RootElement;
            Assert.Equal("unavailable", row.GetProperty("status").GetString());
            Assert.Equal("Rejected 400", row.GetProperty("reason").GetString());
            Assert.Equal(["reasoning_effort"], row.GetProperty("extraBodyKeys").EnumerateArray().Select(e => e.GetString()!).ToArray());
        });
        Assert.DoesNotContain("SECRET-EXTRA-VALUE", File.ReadAllText(Path.Combine(OutsideRepo, "results.jsonl")), StringComparison.Ordinal);
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
