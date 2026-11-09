using System.Text.RegularExpressions;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Chat.Llm;
using Chatbot.Infrastructure.Dev;
using Chatbot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chatbot.Tests.Unit;

/// <summary>Phase 6a: the Llm settings, the allow list, the registration (Fake stays the default) and the system prompt.</summary>
public class LlmConfigurationTests
{
    private const string SecretValue = "sk-SECRET-VALUE-0123456789";

    private static LlmOptions Real(Action<LlmOptions>? tweak = null)
    {
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            BaseUrl = "https://llm.example.test/v1",
            AllowedHosts = ["llm.example.test"],
            Model = "test-model-x",
            ApiKey = SecretValue,
        };
        tweak?.Invoke(options);
        return options;
    }

    // ---------------------------------------------------------------- the settings

    [Fact]
    public void TheDefault_IsFake_AndNeedsNothing()
    {
        var options = new LlmOptions();
        Assert.Equal("Fake", options.Provider);
        Assert.False(options.IsReal);
        Assert.Empty(OptionsValidator.ValidateLlm(options, development: false));
        Assert.Empty(OptionsValidator.ValidateLlm(options, development: true));
    }

    [Fact]
    public void AnUnknownProvider_IsRefused()
    {
        var problems = OptionsValidator.ValidateLlm(new LlmOptions { Provider = "OpenAI" }, development: false);
        Assert.Contains(problems, p => p.Contains("Llm:Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void AFullyConfiguredProvider_Passes()
    {
        Assert.Empty(OptionsValidator.ValidateLlm(Real(), development: false));
        Assert.Empty(OptionsValidator.ValidateLlm(Real(o => o.Provider = "openaicompatible"), development: false)); // the name is not case sensitive
    }

    [Fact]
    public void ARealProvider_NeedsKeyModelAndAnAllowedHost_AndTheMessagesNameSettingsOnly()
    {
        var problems = OptionsValidator.ValidateLlm(new LlmOptions { Provider = "OpenAiCompatible" }, development: false);

        foreach (var setting in new[] { "Llm:BaseUrl", "Llm:AllowedHosts", "Llm:ApiKey", "Llm:Model" })
        {
            Assert.Contains(problems, p => p.Contains(setting, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("https://other.example.test/v1")] // not on the allow list
    [InlineData("http://llm.example.test/v1")] // plain http outside localhost
    [InlineData("https://user:secretpw@llm.example.test/v1")] // credentials in the URL
    [InlineData("https://llm.example.test/v1?key=abc")] // a query string
    [InlineData("ftp://llm.example.test/v1")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void ABaseUrlOffTheRules_IsRefused_WithoutRepeatingIt(string baseUrl)
    {
        var problems = OptionsValidator.ValidateLlm(Real(o => o.BaseUrl = baseUrl), development: false);

        Assert.NotEmpty(problems);
        Assert.All(problems, p =>
        {
            Assert.DoesNotContain("secretpw", p, StringComparison.Ordinal);
            Assert.DoesNotContain("other.example.test", p, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, p, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.example.test")]
    [InlineData("https://llm.example.test")]
    [InlineData("llm.example.test:443")]
    [InlineData("llm.example.test/v1")]
    public void AnAllowListThatIsNotPlainHostNames_IsRefused(string entry)
    {
        var problems = OptionsValidator.ValidateLlm(Real(o => o.AllowedHosts = [entry, "llm.example.test"]), development: false);
        Assert.Contains(problems, p => p.Contains("Llm:AllowedHosts", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHostMatch_IsExact_NotASuffix_AndNotCaseSensitive()
    {
        Assert.Empty(OptionsValidator.ValidateLlm(Real(o => o.BaseUrl = "https://LLM.Example.TEST/v1"), development: false));
        Assert.NotEmpty(OptionsValidator.ValidateLlm(Real(o => o.BaseUrl = "https://evil-llm.example.test/v1"), development: false));
        Assert.NotEmpty(OptionsValidator.ValidateLlm(Real(o => o.BaseUrl = "https://llm.example.test.evil.test/v1"), development: false));
    }

    [Fact]
    public void PlainHttp_IsAcceptedOnlyForLocalhostInDevelopment()
    {
        Action<LlmOptions> local = o =>
        {
            o.BaseUrl = "http://localhost:8000/v1";
            o.AllowedHosts = ["localhost"];
        };

        Assert.Empty(OptionsValidator.ValidateLlm(Real(local), development: true));
        Assert.Contains(OptionsValidator.ValidateLlm(Real(local), development: false), p => p.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void LimitsAreChecked_AndTheMessagesNameTheSetting()
    {
        var problems = OptionsValidator.ValidateLlm(
            Real(o =>
            {
                o.Temperature = 5;
                o.MaxTokens = 0;
                o.FirstTokenTimeoutSeconds = 0;
                o.MaxRetries = 99;
                o.MaxConcurrentRequests = 0;
                o.MaxTokensParameter = "max tokens; drop table";
                o.Model = "bad model name!";
            }),
            development: false);

        foreach (var setting in new[] { "Llm:Temperature", "Llm:MaxTokens", "Llm:FirstTokenTimeoutSeconds", "Llm:MaxRetries", "Llm:MaxConcurrentRequests", "Llm:MaxTokensParameter", "Llm:Model" })
        {
            Assert.Contains(problems, p => p.Contains(setting, StringComparison.Ordinal));
        }

        Assert.All(problems, p => Assert.DoesNotContain(SecretValue, p, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the registration

    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] extra)
    {
        var settings = TestServices.Secrets();
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static (string, string)[] RealSettings() =>
    [
        ("Llm:Provider", "OpenAiCompatible"),
        ("Llm:BaseUrl", "https://llm.example.test/v1"),
        ("Llm:AllowedHosts:0", "llm.example.test"),
        ("Llm:Model", "test-model-x"),
        ("Llm:ApiKey", SecretValue),
    ];

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void WithNoLlmSettings_TheFakeResponderIsRegistered_AndNoNetworkClientExists(string environment)
    {
        using var provider = TestServices.Build(environment);
        var responder = provider.GetRequiredService<IChatResponder>();

        Assert.IsType<FakeChatResponder>(responder);
        Assert.Null(responder.PromptVersion);
    }

    [Fact]
    public void WithTheModelProviderSet_TheOpenAiCompatibleResponderIsRegistered_WithThePromptVersion()
    {
        using var provider = TestServices.Build("Production", Settings(RealSettings()));
        var responder = provider.GetRequiredService<IChatResponder>();

        Assert.IsType<OpenAiCompatibleChatResponder>(responder);
        Assert.Equal(provider.GetRequiredService<ISystemPromptProvider>().Version, responder.PromptVersion);
    }

    [Fact]
    public void InDevelopment_TheModelResponderIsWrappedByTheTriggerDecorator_AndKeepsItsPromptVersion()
    {
        using var provider = TestServices.Build(Environments.Development, Settings(RealSettings()));
        var responder = provider.GetRequiredService<IChatResponder>();

        Assert.IsType<DevTriggerChatResponder>(responder);
        Assert.Equal(provider.GetRequiredService<ISystemPromptProvider>().Version, responder.PromptVersion);
    }

    [Fact]
    public void AModelProviderThatIsNotAllowed_CannotBeResolved()
    {
        using var provider = TestServices.Build("Production", Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:BaseUrl", "https://abroad.example.test/v1"),
            ("Llm:AllowedHosts:0", "llm.example.test"),
            ("Llm:Model", "test-model-x"),
            ("Llm:ApiKey", SecretValue)));

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChatResponder>());
        Assert.DoesNotContain("abroad.example.test", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptions_AreBoundFromConfiguration_AndTheKeyIsNeverPartOfAnyValidationMessage()
    {
        using var provider = TestServices.Build("Production", Settings(RealSettings()));
        var options = provider.GetRequiredService<LlmOptions>();

        Assert.True(options.IsReal);
        Assert.Equal("test-model-x", options.Model);
        Assert.Equal(["llm.example.test"], options.AllowedHosts);
        Assert.Equal(SecretValue, options.ApiKey);
        Assert.All(OptionsValidator.ValidateLlm(options, development: false), p => Assert.DoesNotContain(SecretValue, p, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the system prompt

    private static string RawPrompt()
    {
        using var stream = typeof(SystemPromptText).Assembly.GetManifestResourceStream(SystemPromptText.ResourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ThePrompt_ShipsInsideTheAssembly_AndItsVersionIsALabelPlusAHashOfTheText()
    {
        var prompt = SystemPromptText.LoadEmbedded();

        Assert.Matches(new Regex("^v1\\+[0-9a-f]{8}$"), prompt.Version);
        Assert.True(prompt.Version.Length <= 64);
        Assert.Equal(prompt.Version, SystemPromptText.LoadEmbedded().Version); // stable

        var edited = new SystemPromptText(RawPrompt() + "\nbir cümle daha");
        Assert.NotEqual(prompt.Version, edited.Version); // an edit that forgot the label still gets a new version
        Assert.StartsWith("v1+", edited.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTodoMarks_StayInTheFile_ButAreNeverSentToTheModel()
    {
        Assert.Contains("TODO(volkan): metin onayı", RawPrompt(), StringComparison.Ordinal);
        Assert.Contains("TODO(volkan): klinik uzman onayı", RawPrompt(), StringComparison.Ordinal);

        var text = SystemPromptText.LoadEmbedded().Text;
        Assert.DoesNotContain("TODO", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
        Assert.DoesNotContain("version:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrompt_CarriesTheRules_TheFourPeplauPhases_AndTheAiReminder()
    {
        var text = SystemPromptText.LoadEmbedded().Text;

        foreach (var phrase in new[]
        {
            "yapay zekâ", "acil servisin yerini tutmadığını", "Türkçe", "112",
            "Yönelim", "Tanımlama", "Yararlanma", "Çözülme", "Peplau",
            "tanı koyma", "ilaç", "prognoz", "Beslenme ve egzersiz önerisi verme",
            "her şey düzelecek",
        })
        {
            Assert.Contains(phrase, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ThePrompt_HasNoExerciseOrFoodAdvice_ButKeepsTheLightNonMedicalCopingIdeas()
    {
        var text = SystemPromptText.LoadEmbedded().Text;

        // the NIC examples like "10 dakika yürüyüş" are gone
        foreach (var advice in new[] { "yürüyüş", "dakika yürü", "spor yap", "egzersiz yap", "su iç", "protein", "sebze", "uyku düzeni", "ortam sıcaklığı" })
        {
            Assert.DoesNotContain(advice, text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var idea in new[] { "nefes", "yazmak", "yakın biriyle konuşmak", "mola" })
        {
            Assert.Contains(idea, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ThePrompt_IsShort_SoTheReplyToo()
    {
        var text = SystemPromptText.LoadEmbedded().Text;
        Assert.InRange(text.Length, 500, 6000);
        Assert.Contains("3-5 cümle", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the stored version

    [Fact]
    public void TheColumn_HoldsAnyVersionString_NoLengthLimitInTheModelNorInTheScript()
    {
        using var db = TestEncryption.InMemoryContext();
        var property = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Message))!.FindProperty(nameof(Message.SystemPromptVersion))!;
        Assert.Null(property.GetMaxLength());
        Assert.True(property.IsNullable);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        var script = File.ReadAllText(Path.Combine(dir!.FullName, "db", "001_initial.sql"));
        Assert.Matches(new Regex("system_prompt_version\\s+text\\s+NULL"), script); // text: no length limit

        // and what is written there fits trivially
        Assert.True(SystemPromptText.LoadEmbedded().Version.Length < 64);
        Assert.True(FallbackReply.Version.Length < 64);
    }

    [Fact]
    public void TheEvaluationFolder_IsIgnoredByGit()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chatbot.sln")))
        {
            dir = dir.Parent;
        }

        // api/ is also a repository of its own (LumindApi), so its .gitignore must hold the rule too; the monorepo root has it as well.
        var apiLines = File.ReadAllLines(Path.Combine(dir!.FullName, ".gitignore")).Select(l => l.Trim()).ToList();
        Assert.Contains("/eval-out/", apiLines);

        var rootIgnore = Path.Combine(dir.Parent!.FullName, ".gitignore");
        if (File.Exists(rootIgnore))
        {
            Assert.Contains("api/eval-out/", File.ReadAllLines(rootIgnore).Select(l => l.Trim()));
        }
    }
}
