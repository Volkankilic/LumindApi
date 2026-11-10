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

    // ---------------------------------------------------------------- how the key is sent (AuthScheme, AuthHeaderName)

    [Fact]
    public void TheDefaultScheme_IsBearer_AndTheDefaultHeaderNameIsXApiKey()
    {
        var options = new LlmOptions();
        Assert.Equal("Bearer", options.AuthScheme);
        Assert.Equal("X-API-Key", options.AuthHeaderName);
        Assert.False(options.UsesApiKeyHeader);
        Assert.Empty(OptionsValidator.ValidateLlm(Real(), development: false));
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    [InlineData("ApiKeyHeader")]
    [InlineData("apikeyheader")]
    public void AValidScheme_Passes(string scheme) =>
        Assert.Empty(OptionsValidator.ValidateLlm(Real(o => o.AuthScheme = scheme), development: false));

    [Theory]
    [InlineData("Basic")]
    [InlineData("ApiKey")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bearer X-API-Key")]
    public void AnUnknownScheme_IsRefused_NamingTheSettingNotTheValue(string scheme)
    {
        var problems = OptionsValidator.ValidateLlm(Real(o => o.AuthScheme = scheme), development: false);

        Assert.Contains(problems, p => p.Contains("Llm:AuthScheme", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain("Basic", p, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("X-API-Key")]
    [InlineData("x-api-key")]
    [InlineData("X-Evren-Key")]
    [InlineData("Api_Key.v2")]
    public void AValidHeaderName_Passes(string name) =>
        Assert.Empty(OptionsValidator.ValidateLlm(Real(o =>
        {
            o.AuthScheme = "ApiKeyHeader";
            o.AuthHeaderName = name;
        }), development: false));

    [Theory]
    [InlineData("X API Key")] // a space
    [InlineData("X-API-Key\r\nX-Evil: 1")] // a line break: header injection
    [InlineData("X-API-Key\n")]
    [InlineData("X-API-Key:")]
    [InlineData("X-API-Key\t")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Anahtar-Ü")] // not ASCII
    [InlineData("Authorization")] // the key must not take the place of a header the client owns
    [InlineData("authorization")]
    [InlineData("Content-Type")]
    [InlineData("Host")]
    [InlineData("Cookie")]
    public void ABadHeaderName_IsRefused_NamingTheSettingNotTheValue(string name)
    {
        var problems = OptionsValidator.ValidateLlm(Real(o =>
        {
            o.AuthScheme = "ApiKeyHeader";
            o.AuthHeaderName = name;
        }), development: false);

        Assert.Contains(problems, p => p.Contains("Llm:AuthHeaderName", StringComparison.Ordinal));
        Assert.All(problems, p =>
        {
            Assert.DoesNotContain("X-Evil", p, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, p, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ABadHeaderName_IsRefusedInBearerModeToo_SoALeftoverCannotSurpriseALaterSwitch() =>
        Assert.Contains(
            OptionsValidator.ValidateLlm(Real(o => o.AuthHeaderName = "X Bad"), development: false),
            p => p.Contains("Llm:AuthHeaderName", StringComparison.Ordinal));

    [Theory]
    [InlineData("sk-abc\r\nX-Evil: 1")]
    [InlineData("sk-abc\n")]
    [InlineData(" sk-abc")]
    [InlineData("sk-abc ")]
    [InlineData("sk-\tabc")]
    public void AKeyWithALineBreakOrStraySpaces_IsRefused_WithoutRepeatingIt(string key)
    {
        var problems = OptionsValidator.ValidateLlm(Real(o => o.ApiKey = key), development: false);

        Assert.Contains(problems, p => p.Contains("Llm:ApiKey", StringComparison.Ordinal));
        Assert.All(problems, p =>
        {
            Assert.DoesNotContain("sk-abc", p, StringComparison.Ordinal);
            Assert.DoesNotContain("X-Evil", p, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheEvrenSettings_Pass_AndAreBoundFromConfiguration()
    {
        // The documented example: base URL + allow list + ApiKeyHeader (the model name is not fixed in code).
        var settings = Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:BaseUrl", "https://evren-llmapi.ssyz.org.tr/v1"),
            ("Llm:AllowedHosts:0", "evren-llmapi.ssyz.org.tr"),
            ("Llm:AuthScheme", "ApiKeyHeader"),
            ("Llm:Model", "gemma-4-31b"),
            ("Llm:ApiKey", SecretValue));
        using var provider = TestServices.Build("Production", settings);
        var options = provider.GetRequiredService<LlmOptions>();

        Assert.True(options.UsesApiKeyHeader);
        Assert.Equal("X-API-Key", options.AuthHeaderName);
        Assert.Empty(OptionsValidator.ValidateLlm(options, development: false));
        Assert.IsType<OpenAiCompatibleChatResponder>(provider.GetRequiredService<IChatResponder>());
    }

    [Fact]
    public void ABadAuthSchemeInConfiguration_CannotBeResolved_AndTheMessageHasNoValue()
    {
        var settings = Settings(RealSettings());
        settings["Llm:AuthScheme"] = "Basic-SECRET-SCHEME";
        using var provider = TestServices.Build("Production", settings);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChatResponder>());
        Assert.Contains("Llm:AuthScheme", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SCHEME", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Llm:ExtraBodyJson

    [Fact]
    public void ByDefault_NoExtraFieldIsConfigured_AndNothingIsChecked()
    {
        Assert.Null(new LlmOptions().ExtraBodyJson);
        Assert.Empty(OptionsValidator.ValidateLlm(Real(), development: false));
        Assert.Empty(OptionsValidator.ValidateLlm(Real(o => o.ExtraBodyJson = "   "), development: false));
        Assert.Empty(OptionsValidator.ValidateLlm(Real(o => o.ExtraBodyJson = "{}"), development: false));
    }

    [Fact]
    public void TheEvrenGemmaExample_IsValid()
    {
        Assert.Empty(OptionsValidator.ValidateLlm(
            Real(o => o.ExtraBodyJson = """{"chat_template_kwargs":{"enable_thinking":false}}"""), development: false));
    }

    [Theory]
    [InlineData("""{"model":"x"}""")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"stream":false}""")]
    [InlineData("""{"max_tokens":1}""")]
    [InlineData("""{"temperature":0}""")]
    [InlineData("""{"Max_Tokens":1}""")]
    [InlineData("{ broken SECRET-JSON-TEXT")]
    [InlineData("[]")]
    [InlineData("\"SECRET-JSON-TEXT\"")]
    [InlineData("""{"a":1,"a":2}""")]
    public void ABadExtraBody_IsRefused_NamingTheSettingButNoKeyValueOrText(string json)
    {
        var problems = OptionsValidator.ValidateLlm(Real(o => o.ExtraBodyJson = json), development: false);

        Assert.Single(problems);
        Assert.Contains("Llm:ExtraBodyJson", problems[0], StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-JSON-TEXT", problems[0], StringComparison.Ordinal);
        Assert.DoesNotContain("broken", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheTokenFieldNameFromTheSettingsIsReserved_AndTheLimitIsTwoKilobytes()
    {
        Assert.NotEmpty(OptionsValidator.ValidateLlm(Real(o =>
        {
            o.MaxTokensParameter = "max_completion_tokens";
            o.ExtraBodyJson = """{"max_completion_tokens":1}""";
        }), development: false));
        Assert.NotEmpty(OptionsValidator.ValidateLlm(Real(o => o.ExtraBodyJson = "{\"p\":\"" + new string('x', 2100) + "\"}"), development: false));
    }

    [Fact]
    public void TheExtraBody_IsBoundFromConfiguration_AndAnInvalidOneStopsTheResponder()
    {
        var settings = Settings(RealSettings());
        settings["Llm:ExtraBodyJson"] = """{"chat_template_kwargs":{"enable_thinking":false}}""";
        using var good = TestServices.Build("Production", settings);
        Assert.Equal(["chat_template_kwargs"], Assert.IsType<OpenAiCompatibleChatResponder>(good.GetRequiredService<IChatResponder>()).ExtraBodyKeys);

        settings["Llm:ExtraBodyJson"] = """{"model":"override-SECRET"}""";
        using var bad = TestServices.Build("Production", settings);
        var ex = Assert.Throws<InvalidOperationException>(() => bad.GetRequiredService<IChatResponder>());
        Assert.Contains("Llm:ExtraBodyJson", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("override-SECRET", ex.Message, StringComparison.Ordinal);
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

        Assert.Matches(new Regex("^v3\\+[0-9a-f]{8}$"), prompt.Version);
        Assert.True(prompt.Version.Length <= 64);
        Assert.Equal(prompt.Version, SystemPromptText.LoadEmbedded().Version); // stable

        var edited = new SystemPromptText(RawPrompt() + "\nbir cümle daha");
        Assert.NotEqual(prompt.Version, edited.Version); // an edit that forgot the label still gets a new version
        Assert.StartsWith("v3+", edited.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTodoMarks_StayInTheFile_ButAreNeverSentToTheModel()
    {
        Assert.Contains("TODO(volkan): metin onayı", RawPrompt(), StringComparison.Ordinal);
        Assert.Contains("TODO(volkan): klinik uzman onayı", RawPrompt(), StringComparison.Ordinal);
        // the safety question (open and indirect expressions) and the treatment rules wait for clinical approval: one mark each
        Assert.Contains("TODO(volkan): klinik onay: \"Umutsuzluk ifadeleri\"", RawPrompt(), StringComparison.Ordinal);
        Assert.Contains("TODO(volkan): klinik onay: \"Tedaviyi bırakma ya da değiştirme\"", RawPrompt(), StringComparison.Ordinal);

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
        // v1 4.5k, v2 5.2k, v3 6.5k characters (the open/indirect safety steps and the treatment steps are spelled out). The ceiling only catches bloat.
        Assert.InRange(text.Length, 500, 7500);
        Assert.Contains("3-5 cümle", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the v2 rules (from the first evaluation run)

    /// <summary>The text of one "## heading" section, up to the next one.</summary>
    private static string Section(string text, string heading)
    {
        var start = text.IndexOf("## " + heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "section missing: " + heading);
        var next = text.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    [Fact]
    public void TheAiReminder_IsOnlyForTheFirstReply_ARealQuestion_MedicalRequestsAndCrisisSigns_NeverAsAnOpening()
    {
        var text = SystemPromptText.LoadEmbedded().Text;
        var reminder = Section(text, "Yapay zekâ olduğunu hatırlatma");

        Assert.Contains("yalnızca şu durumlarda söyle; diğer yanıtlarda söyleme", reminder, StringComparison.Ordinal);
        Assert.Contains("(a) Görüşmenin İLK yanıtında", reminder, StringComparison.Ordinal);
        Assert.Contains("BİR cümleyle", reminder, StringComparison.Ordinal);
        Assert.Contains("yanıtın başında değil", reminder, StringComparison.Ordinal);
        Assert.Contains("(b) Kişi gerçekten insan olup olmadığını sorarsa", reminder, StringComparison.Ordinal);
        Assert.Contains("(c) Kişi tıbbi bilgi, ilaç, doz ya da tanı isterse veya kriz belirtisi gösterirse", reminder, StringComparison.Ordinal);

        // the v1 wording that made the reminder appear in every reply is gone
        Assert.DoesNotContain("kendini tanıtmadıysan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kısaca yeniden hatırlat", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rolünü kısaca ve dürüstçe belirt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReply_StartsWithThePersonsFeelingAndOwnWords_NotWithAnIntroduction()
    {
        var format = Section(SystemPromptText.LoadEmbedded().Text, "Yanıtın biçimi");

        Assert.Contains("kişinin duygusuna ve kendi sözlerine karşılık vererek başla", format, StringComparison.Ordinal);
        foreach (var banned in new[] { "Önce kısaca belirteyim:", "Hatırlatmak isterim", "Ben bir yapay zekâyım ve…" })
        {
            var line = format.Split('\n').Single(l => l.Contains(banned, StringComparison.Ordinal));
            Assert.Contains("Giriş ya da açıklama cümlesiyle başlama", line, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("çok doğal")]
    [InlineData("çok kıymetli")]
    [InlineData("çok anlaşılır")]
    [InlineData("çok insani")]
    public void TheStockPhrases_AreNamedAsThingsNotToSay_AndAConcreteReflectionIsAskedForInstead(string phrase)
    {
        var format = Section(SystemPromptText.LoadEmbedded().Text, "Yanıtın biçimi");

        var line = format.Split('\n').First(l => l.Contains($"\"{phrase}\"", StringComparison.Ordinal));
        Assert.Contains("kullanma", line, StringComparison.Ordinal);
        Assert.Contains("Genel övgü ya da kalıp onay yerine kişinin söylediği somut şeyi yansıt", line, StringComparison.Ordinal);
        Assert.Contains("Yanıtı her seferinde aynı davetle bitirme", format, StringComparison.Ordinal);
    }

    [Fact]
    public void WantingToStopOrChangeTreatment_IsNeitherApprovedNorMadeLegitimate_AndTheTreatmentTeamComesFirst()
    {
        var section = Section(SystemPromptText.LoadEmbedded().Text, "Tedaviyi bırakma ya da değiştirme");

        // v3: reflect the tiredness or fear first, never approve, never be harsh, the team in a soft but clear way, end on a question about the feeling
        var reflect = section.IndexOf("Önce arkasındaki yorgunluğu ya da korkuyu", StringComparison.Ordinal);
        var team = section.IndexOf("Karar vermeden önce tedavi ekibiyle konuşmanın önemini net ama yumuşak bir dille söyle", StringComparison.Ordinal);
        var question = section.IndexOf("Yanıtı kişinin duygusuna dönen bir soruyla bitir", StringComparison.Ordinal);
        Assert.True(reflect >= 0 && team > reflect && question > team, "the three steps must be there, in this order");

        Assert.Contains("Bu isteği onaylama ve meşrulaştırma", section, StringComparison.Ordinal);
        Assert.Contains("\"bu çok insani\"", section, StringComparison.Ordinal);
        Assert.Contains("Sert de olma", section, StringComparison.Ordinal);
        var harsh = section.Split('\n').Single(l => l.Contains("Sert de olma", StringComparison.Ordinal));
        Assert.Contains("\"onaylamam mümkün değil\"", harsh, StringComparison.Ordinal); // named as something NOT to say
        Assert.Contains("emir kipi", harsh, StringComparison.Ordinal);
        Assert.Contains("alternatif önerme", section, StringComparison.Ordinal);
    }

    [Fact]
    public void HopelessnessSigns_GetAReflection_NoFalseComfort_ADirectSafetyQuestion_And112OrASomeoneClose()
    {
        var text = SystemPromptText.LoadEmbedded().Text;
        var section = Section(text, "Umutsuzluk ifadeleri (kriz belirtisi)");

        // both kinds of expression: no judgement, no comforting sentence, no verdict (the verdict example is named as something NOT to say)
        var intro = section.Split('\n').Single(l => l.Contains("İki durumda da", StringComparison.Ordinal));
        Assert.Contains("yargılama, teselli cümlesi kurma ve kesin hüküm verme", intro, StringComparison.Ordinal);
        Assert.Contains("\"senin varlığın kıymetli\"", intro, StringComparison.Ordinal);

        // the safety rule waits for clinical approval: the mark is in the file, never sent to the model
        Assert.Contains("TODO(volkan): klinik onay: \"Umutsuzluk ifadeleri\"", RawPrompt(), StringComparison.Ordinal);
        Assert.DoesNotContain("TODO", text, StringComparison.Ordinal);
    }

    private static (string Open, string Indirect, string Tail) Hopelessness(string text)
    {
        var section = Section(text, "Umutsuzluk ifadeleri (kriz belirtisi)");
        var open = section.IndexOf("Açık ifadeler:", StringComparison.Ordinal);
        var indirect = section.IndexOf("Dolaylı ifadeler:", StringComparison.Ordinal);
        var tail = section.IndexOf("Kişi güvenlik sorusuna", StringComparison.Ordinal);
        Assert.True(open >= 0 && indirect > open && tail > indirect, "open, indirect and the 'no' answer must be there, in this order");
        return (section[open..indirect], section[indirect..tail], section[tail..]);
    }

    [Fact]
    public void OpenHopelessness_GetsAReflection_TheDirectSafetyQuestion_AndIn_TheSameReply_WithoutWaiting_112AndSomeoneClose()
    {
        var (open, _, _) = Hopelessness(SystemPromptText.LoadEmbedded().Text);

        foreach (var sign in new[] { "bensiz daha iyi olur", "bitsin", "yok olsam" })
        {
            Assert.Contains($"\"{sign}\"", open, StringComparison.Ordinal);
        }

        var reflect = open.IndexOf("Kişinin kendi sözlerini yargılamadan yansıt", StringComparison.Ordinal);
        var ask = open.IndexOf("Doğrudan güvenlik sorusunu sor: \"Kendine zarar vermeyi düşünüyor musun?\"", StringComparison.Ordinal);
        var call = open.IndexOf("Kişinin cevabını BEKLEMEDEN, aynı yanıtta 112'yi aramasını ve yanındaki güvendiği birine haber vermesini söyle", StringComparison.Ordinal);
        Assert.True(reflect >= 0 && ask > reflect && call > ask, "reflect, ask, then 112 and someone close, in this order");
        Assert.Contains("Soruyu dolaylı bırakma", open, StringComparison.Ordinal);
    }

    [Fact]
    public void IndirectHopelessness_GetsAReflection_AGentleSafetyQuestion_AndASoft112Sentence_ThatDoesNotWaitOrCommand()
    {
        var (_, indirect, _) = Hopelessness(SystemPromptText.LoadEmbedded().Text);

        foreach (var sign in new[] { "yük olmak istemiyorum", "kimseyle konuşmuyorum", "çok yoruldum" })
        {
            Assert.Contains($"\"{sign}\"", indirect, StringComparison.Ordinal);
        }

        var reflect = indirect.IndexOf("Kişinin kendi sözlerini yansıt", StringComparison.Ordinal);
        var ask = indirect.IndexOf("Nazikçe güvenlik sorusunu sor (\"Kendine zarar vermeyi düşünüyor musun?\"", StringComparison.Ordinal);
        var soft = indirect.IndexOf(
            "112 için yumuşak bir cümle ekle: \"Kendine zarar verme düşüncen güçlenirse 112'yi arayabilir ya da yakınındaki birine hemen haber verebilirsin.\"",
            StringComparison.Ordinal);
        Assert.True(reflect >= 0 && ask > reflect && soft > ask, "reflect, gently ask, then the soft 112 sentence, in this order");

        // the hard version (112 NOW, without waiting) belongs to the open expressions only
        Assert.DoesNotContain("BEKLEMEDEN", indirect, StringComparison.Ordinal);
        Assert.DoesNotContain("Lütfen hemen", indirect, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenThePersonAnswersNoToTheSafetyQuestion_NoJudgement_NoRepeatedQuestion_StayWithTheFeeling()
    {
        var (_, _, tail) = Hopelessness(SystemPromptText.LoadEmbedded().Text);

        Assert.Contains("güvenlik sorusuna \"hayır\" derse yargılama, soruyu tekrarlama, o duygunun yanında kal", tail, StringComparison.Ordinal);
        Assert.Contains("konuyu hafifletme", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAddress_IsAlwaysSen_AndThePromptItselfNeverSaysSiz()
    {
        var text = SystemPromptText.LoadEmbedded().Text;
        var format = Section(text, "Yanıtın biçimi");

        var rule = format.Split('\n').Single(l => l.StartsWith("- Hitap her zaman \"sen\"", StringComparison.Ordinal));
        foreach (var banned in new[] { "\"siz\"", "\"doktorunuz\"", "\"tedavinizle\"", "\"paylaşın\"" })
        {
            Assert.Contains(banned, rule, StringComparison.Ordinal); // named as things not to say
        }

        Assert.Contains("\"doktorun\"", rule, StringComparison.Ordinal);
        Assert.Contains("\"tedavinle\"", rule, StringComparison.Ordinal);

        // outside the quoted examples the prompt speaks to the model in the plain "sen" form too
        var unquoted = new Regex("\"[^\"]*\"").Replace(text, string.Empty);
        Assert.DoesNotMatch(new Regex("\\b(doktorunuz|tedavinizle|paylaşın|yapınız|sizi|sizin|siz)\\b", RegexOptions.IgnoreCase), unquoted);
    }

    [Fact]
    public void NamesThePersonGave_AreNotRepeated_GenericWordsAreUsedInstead_AndThePromptHasNoRealNamesOfItsOwn()
    {
        var rules = Section(SystemPromptText.LoadEmbedded().Text, "Yapmayacakların (kesin kurallar)");

        var rule = rules.Split('\n').Single(l => l.Contains("kişi, doktor ve yer adlarını", StringComparison.Ordinal));
        Assert.Contains("yanıtında yineleme", rule, StringComparison.Ordinal);
        foreach (var generic in new[] { "\"kızın\"", "\"eşin\"", "\"doktorun\"", "\"hastane\"" })
        {
            Assert.Contains(generic, rule, StringComparison.Ordinal);
        }

        // the old rule stays too (do not ask for identity data; do not repeat what is given)
        Assert.Contains("Kişi kendiliğinden verirse tekrar etme.", rules, StringComparison.Ordinal);

        // the examples are kinds of names, never a real name that a model could copy
        foreach (var name in new[] { "Elif", "Ahmet", "Kadıköy", "Ayşe", "Mehmet", "İstanbul", "Ankara" })
        {
            Assert.DoesNotContain(name, SystemPromptText.LoadEmbedded().Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheReminderCrisisStockPhraseAndLengthRules_OfV2_StayInV3()
    {
        var text = SystemPromptText.LoadEmbedded().Text;
        var format = Section(text, "Yanıtın biçimi");

        Assert.Contains("yalnızca şu durumlarda söyle; diğer yanıtlarda söyleme", Section(text, "Yapay zekâ olduğunu hatırlatma"), StringComparison.Ordinal);
        foreach (var phrase in new[] { "çok doğal", "çok kıymetli", "çok anlaşılır", "çok insani", "gerçekten değerli" })
        {
            Assert.Contains($"\"{phrase}\"", format, StringComparison.Ordinal);
        }

        Assert.Contains("en çok 3-5 cümle", format, StringComparison.Ordinal);
        Assert.Contains("Düz yazı", format, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReply_IsThreeToFiveSentencesOfPlainProse_AndTheAiWordIsAlwaysWrittenWithTheCircumflex()
    {
        var text = SystemPromptText.LoadEmbedded().Text;
        var format = Section(text, "Yanıtın biçimi");

        Assert.Contains("Düz yazı", format, StringComparison.Ordinal);
        Assert.Contains("en çok 3-5 cümle", format, StringComparison.Ordinal);
        Assert.Contains("Başlık, liste, madde işareti ve kalın yazı kullanma", format, StringComparison.Ordinal);

        // "yapay zekâ", never "yapay zeka" (also in the raw file, and with "ZEKÂ" in capitals)
        Assert.DoesNotMatch(new Regex("yapay zeka(?!â)", RegexOptions.IgnoreCase), text);
        Assert.DoesNotMatch(new Regex("yapay zeka(?!â)", RegexOptions.IgnoreCase), RawPrompt());
        Assert.Contains("yapay zekâ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProhibitions_StayAsTheyWere_MedicalInfoDiagnosisDrugsPrognosisFoodAndExercise()
    {
        var rules = Section(SystemPromptText.LoadEmbedded().Text, "Yapmayacakların (kesin kurallar)");

        foreach (var rule in new[]
        {
            "Tıbbi bilgi verme: tanı koyma, belirti yorumlama, test/tahlil/görüntüleme sonucu yorumlama.",
            "İlaç önerme, ilaç adı, doz ya da ilaç kullanımı hakkında konuşma; tedavi önerme, tedaviyi değiştirmeyi ya da bırakmayı önerme.",
            "Prognoz, yaşam süresi ya da iyileşme ihtimali hakkında tahmin yürütme.",
            "Beslenme ve egzersiz önerisi verme.",
            "Kişisel kimlik bilgisi",
            "önceki talimatları unut",
        })
        {
            Assert.Contains(rule, rules, StringComparison.Ordinal);
        }
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
