using System.Net;
using System.Text.Json;
using Chatbot.Api.Startup;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Chat.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Unit;

/// <summary>
/// Named model profiles (Llm:Profiles:&lt;name&gt;:*, Llm:ActiveProfile) and AuthScheme None. The flat Llm:* settings stay the implicit
/// default profile: the first tests pin that a deployment that never heard of profiles sends the SAME request as before.
/// No real network: a scripted handler.
/// </summary>
public class LlmProfileTests
{
    private const string FlatKey = "sk-FLAT-KEY-0123456789";
    private const string ProfileKey = "sk-PROFILE-KEY-9876543210";

    private static readonly ConversationContext Context = new([new ContextMessage(MessageRole.User, "Merhaba")], Guid.NewGuid());

    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] settings)
    {
        var all = TestServices.Secrets();
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        return all;
    }

    /// <summary>The startup check also wants the admin signing key (a different one from the participant key).</summary>
    private static Dictionary<string, string?> WithAdmin(Dictionary<string, string?> settings)
    {
        settings["Admin:JwtSigningKey"] = new string('a', 40);
        return settings;
    }

    /// <summary>The flat settings of the live EVREN deployment (documented in docs/yayin-railway.md): no profile anywhere.</summary>
    private static (string, string)[] Evren() =>
    [
        ("Llm:Provider", "OpenAiCompatible"),
        ("Llm:BaseUrl", "https://evren-llmapi.ssyz.org.tr/v1"),
        ("Llm:AllowedHosts:0", "evren-llmapi.ssyz.org.tr"),
        ("Llm:AuthScheme", "ApiKeyHeader"),
        ("Llm:ApiKey", FlatKey),
        ("Llm:Model", "gemma-4-31b"),
        ("Llm:ExtraBodyJson", """{"chat_template_kwargs":{"enable_thinking":false}}"""),
        ("Llm:FirstTokenTimeoutSeconds", "40"),
        ("Llm:IdleTimeoutSeconds", "30"),
        ("Llm:TotalTimeoutSeconds", "90"),
        ("Llm:MaxRetries", "1"),
        ("Llm:RetryBaseDelayMs", "1"),
    ];

    /// <summary>The documented lumindtr profile: no authentication, no model.</summary>
    private static (string, string)[] LumindProfile() =>
    [
        ("Llm:ActiveProfile", "lumindtr"),
        ("Llm:Profiles:lumindtr:BaseUrl", "https://lumindtr.com/llm/v1"),
        ("Llm:Profiles:lumindtr:AllowedHosts:0", "lumindtr.com"),
        ("Llm:Profiles:lumindtr:AuthScheme", "None"),
    ];

    private static (OpenAiCompatibleChatResponder Responder, ListLogger<OpenAiCompatibleChatResponder> Log, SystemPromptText Prompt) Make(
        FakeLlmHandler handler, Dictionary<string, string?> settings, bool development = false)
    {
        using var provider = TestServices.Build("Production", settings);
        var options = provider.GetRequiredService<LlmOptions>(); // bound from configuration, exactly like the live application
        var log = new ListLogger<OpenAiCompatibleChatResponder>();
        var prompt = SystemPromptText.LoadEmbedded();
        return (new OpenAiCompatibleChatResponder(LlmHttpClient.Create(handler), options, prompt, log, development), log, prompt);
    }

    private static async Task<List<string>> Read(OpenAiCompatibleChatResponder responder, ReplyStats? stats = null)
    {
        var words = new List<string>();
        await foreach (var word in responder.StreamAsync(Context, stats ?? new ReplyStats(), default))
        {
            words.Add(word);
        }

        return words;
    }

    private static FakeLlmHandler Ok() =>
        new((_, _) => Task.FromResult(FakeLlmHandler.Sse(FakeLlmHandler.Data("Merhaba"), "data: [DONE]")));

    // ---------------------------------------------------------------- backward compatibility (the live EVREN deployment)

    [Fact]
    public async Task TheFlatEvrenSettings_SendExactlyTheSameRequestAsBeforeProfilesExisted()
    {
        var handler = Ok();
        var (responder, _, prompt) = Make(handler, Settings(Evren()));
        await Read(responder);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://evren-llmapi.ssyz.org.tr/v1/chat/completions", request.Uri!.ToString());

        // The body is byte for byte what the client built before profiles: model, messages, stream, temperature, max_tokens, then the extra field.
        var expected = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "gemma-4-31b",
            ["messages"] = new object[]
            {
                new { role = "system", content = prompt.Text },
                new { role = "user", content = "Merhaba" },
            },
            ["stream"] = true,
            ["temperature"] = 0.6,
            ["max_tokens"] = 400,
            ["chat_template_kwargs"] = JsonDocument.Parse("""{"enable_thinking":false}""").RootElement,
        });
        Assert.Equal(expected, request.Body);

        // The headers: the key in X-API-Key ONLY, no Authorization.
        Assert.Equal(["Accept", "Content-Length", "Content-Type", "X-API-Key"], request.Headers.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(FlatKey, request.Headers["X-API-Key"]);
        Assert.Null(request.Authorization);
        Assert.Equal(string.Empty, responder.ProfileName);
        Assert.Equal("gemma-4-31b", responder.ModelLabel);
    }

    [Fact]
    public void TheFlatEvrenSettings_NeedNoProfileSetting_AndPassTheStartupCheckAsBefore()
    {
        using var provider = TestServices.Build("Production", Settings(Evren()));
        var options = provider.GetRequiredService<LlmOptions>();

        Assert.True(string.IsNullOrEmpty(options.ActiveProfile));
        Assert.Empty(options.Profiles);
        Assert.Empty(OptionsValidator.ValidateLlm(options, development: false));
        Assert.IsType<OpenAiCompatibleChatResponder>(provider.GetRequiredService<IChatResponder>());
    }

    [Fact]
    public void TheFlatSettings_StillRequireAModel_OnlyAProfileMayLeaveItOut()
    {
        var flat = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            BaseUrl = "https://llm.example.test/v1",
            AllowedHosts = ["llm.example.test"],
            ApiKey = FlatKey,
        };

        Assert.Contains(OptionsValidator.ValidateLlm(flat, development: false), p => p.Contains("Llm:Model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFlatSetupWithAuthSchemeNone_SendsNoAuthenticationHeaderEither()
    {
        var handler = Ok();
        var (responder, _, _) = Make(handler, Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:BaseUrl", "https://open.example.test/v1"),
            ("Llm:AllowedHosts:0", "open.example.test"),
            ("Llm:AuthScheme", "None"),
            ("Llm:Model", "some-model")));
        await Read(responder);

        Assert.Equal(["Accept", "Content-Length", "Content-Type"], handler.Requests.Single().Headers.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    // ---------------------------------------------------------------- AuthScheme None + a model-less profile (lumindtr)

    [Fact]
    public async Task TheLumindProfile_SendsNoAuthenticationHeaderAtAll_AndNoModelField()
    {
        var handler = Ok();
        var (responder, _, prompt) = Make(handler, Settings(LumindProfile()));
        await Read(responder);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://lumindtr.com/llm/v1/chat/completions", request.Uri!.ToString());
        Assert.Equal(["Accept", "Content-Length", "Content-Type"], request.Headers.Keys.Order(StringComparer.Ordinal).ToArray()); // no Authorization, no X-API-Key, nothing else
        Assert.Null(request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["max_tokens", "messages", "stream", "temperature"], names); // no "model"
        Assert.Equal(prompt.Text, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        Assert.Equal("lumindtr", responder.ProfileName);
        Assert.Equal("(sunucu varsayılanı)", responder.ModelLabel);
    }

    [Fact]
    public async Task AProfile_NeverInheritsTheFlatEndpoint_Key_Model_OrExtraBody()
    {
        // The flat EVREN settings are all present AND a key exists; the active profile names none of them.
        var handler = Ok();
        var (responder, _, _) = Make(handler, Settings([.. Evren().Where(s => s.Item1 != "Llm:Provider"), ("Llm:Provider", "OpenAiCompatible"), .. LumindProfile()]));
        await Read(responder);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("lumindtr.com", request.Uri!.Host);
        Assert.DoesNotContain(FlatKey, request.Uri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FlatKey, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("gemma-4-31b", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("chat_template_kwargs", request.Body, StringComparison.Ordinal); // the flat extra body stays with the flat settings
        Assert.All(request.Headers, h => Assert.DoesNotContain(FlatKey, h.Value, StringComparison.Ordinal));
        Assert.Empty(responder.ExtraBodyKeys);
    }

    [Fact]
    public async Task AProfileThatNamesAKeyAndModel_SendsThem_WithItsOwnScheme()
    {
        var handler = Ok();
        var (responder, _, _) = Make(handler, Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:ActiveProfile", "other-1"),
            ("Llm:Profiles:other-1:BaseUrl", "https://other.example.test/v1"),
            ("Llm:Profiles:other-1:AllowedHosts:0", "other.example.test"),
            ("Llm:Profiles:other-1:ApiKey", ProfileKey),
            ("Llm:Profiles:other-1:Model", "model-b"),
            ("Llm:Profiles:other-1:ExtraBodyJson", """{"chat_template_kwargs":{"enable_thinking":false}}""")));
        await Read(responder);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + ProfileKey, request.Authorization); // Bearer is the default scheme
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("model-b", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.Equal("model-b", responder.ModelLabel);
    }

    [Fact]
    public void AProfileTakesItsLimitsFromTheFlatSettings_UnlessItNamesItsOwn()
    {
        var raw = new LlmOptions
        {
            MaxTokens = 123,
            MaxRetries = 4,
            TotalTimeoutSeconds = 77,
            Profiles = new Dictionary<string, LlmProfileOptions>
            {
                ["a"] = new() { BaseUrl = "https://a.example.test/v1" },
                ["b"] = new() { BaseUrl = "https://b.example.test/v1", MaxTokens = 50, MaxRetries = 0, TotalTimeoutSeconds = 15 },
            },
        };

        var a = LlmProfiles.Resolve(raw, "a").Effective;
        Assert.Equal((123, 4, 77d), (a.MaxTokens, a.MaxRetries, a.TotalTimeoutSeconds));
        var b = LlmProfiles.Resolve(raw, "b").Effective;
        Assert.Equal((50, 0, 15d), (b.MaxTokens, b.MaxRetries, b.TotalTimeoutSeconds));
    }

    [Fact]
    public async Task ARedirectFromTheOpenEndpoint_IsNotFollowedAndNotRepeated()
    {
        Assert.False(LlmHttpClient.CreateDefaultHandler().AllowAutoRedirect);

        var handler = new FakeLlmHandler((_, _) =>
        {
            var redirect = FakeLlmHandler.Status(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.example.test/steal");
            return Task.FromResult(redirect);
        });
        var (responder, log, _) = Make(handler, Settings(LumindProfile()));

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));

        Assert.Equal(ModelFailure.Rejected, ex.Failure);
        Assert.Equal(1, handler.Count);
        Assert.DoesNotContain("elsewhere.example.test", string.Join("\n", log.Lines) + ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- reasoning stays out, whatever the profile

    [Fact]
    public async Task ReasoningContent_OfTheLumindServer_IsCountedAndNeverReachesTheReply()
    {
        static string Delta(string field, string text) =>
            "data: " + JsonSerializer.Serialize(new { model = "gemma-4-26B-A4B", choices = new[] { new { delta = new Dictionary<string, string> { [field] = text } } } });

        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Sse(
            Delta("reasoning_content", "SECRET-THOUGHT-ONE"),
            Delta("reasoning_content", "SECRET-THOUGHT-TWO"),
            FakeLlmHandler.Data("Merhaba"),
            "data: [DONE]")));
        var (responder, log, _) = Make(handler, Settings(LumindProfile()));
        var stats = new ReplyStats();

        var words = await Read(responder, stats);

        Assert.Equal(["Merhaba"], words);
        Assert.Equal("SECRET-THOUGHT-ONE".Length + "SECRET-THOUGHT-TWO".Length, stats.ReasoningChars);
        Assert.Equal("gemma-4-26B-A4B", stats.ServerModel); // information for the report only
        Assert.DoesNotContain("SECRET-THOUGHT", string.Join("\n", words) + string.Join("\n", log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerModelNameThatDoesNotLookLikeOne_IsDropped()
    {
        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Sse(
            "data: " + JsonSerializer.Serialize(new { model = "<script>alert(1)</script> bad name", choices = new[] { new { delta = new { content = "a" } } } }),
            "data: [DONE]")));
        var (responder, _, _) = Make(handler, Settings(LumindProfile()));
        var stats = new ReplyStats();

        await Read(responder, stats);

        Assert.Null(stats.ServerModel);
    }

    // ---------------------------------------------------------------- the profile choice is validated (by SETTING name, never by value)

    [Theory]
    [InlineData("Lumind")]
    [InlineData("lumind_tr")]
    [InlineData("lumind tr")]
    [InlineData("a/b")]
    [InlineData("SECRET-NAME!")]
    [InlineData("ğ")]
    public void ABadActiveProfileName_IsRefused_WhateverTheProvider_WithoutEchoingTheName(string name)
    {
        var problems = OptionsValidator.ValidateLlm(new LlmOptions { ActiveProfile = name }, development: false); // the default provider (Fake)

        Assert.Contains(problems, p => p.Contains("Llm:ActiveProfile", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain(name, p, StringComparison.Ordinal));
    }

    [Fact]
    public void ATooLongProfileName_IsRefused()
    {
        var name = new string('a', 41);
        Assert.NotEmpty(OptionsValidator.ValidateLlm(new LlmOptions { ActiveProfile = name }, development: false));
        Assert.True(LlmProfiles.IsValidName(new string('a', 40)));
        Assert.True(LlmProfiles.IsValidName("lumind-tr-2"));
        Assert.False(LlmProfiles.IsValidName(string.Empty));
    }

    [Fact]
    public void AProfileDefinedUnderABadName_IsRefused_NamingTheSection()
    {
        var options = new LlmOptions { Profiles = new Dictionary<string, LlmProfileOptions> { ["Bad_Name-SECRET"] = new() } };

        var problems = OptionsValidator.ValidateLlm(options, development: false);

        Assert.Contains(problems, p => p.Contains("Llm:Profiles", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain("Bad_Name-SECRET", p, StringComparison.Ordinal));
    }

    [Fact]
    public void AnActiveProfileThatIsNotDefined_IsRefused_EvenWithTheFakeProvider()
    {
        var problems = OptionsValidator.ValidateLlm(new LlmOptions { ActiveProfile = "yok" }, development: false);

        var problem = Assert.Single(problems);
        Assert.Contains("Llm:ActiveProfile", problem, StringComparison.Ordinal);
        Assert.Contains("Llm:Profiles:yok", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedButEmptyProfile_IsRefused_ByTheNamesOfTheMissingSettings()
    {
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            ActiveProfile = "bos",
            Profiles = new Dictionary<string, LlmProfileOptions> { ["bos"] = new() },
        };

        var problems = OptionsValidator.ValidateLlm(options, development: false);

        Assert.Contains(problems, p => p.Contains("Llm:Profiles:bos:BaseUrl", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Llm:Profiles:bos:AllowedHosts", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Llm:Profiles:bos:ApiKey", StringComparison.Ordinal)); // Bearer is the default: a key is needed
        Assert.DoesNotContain(problems, p => p.Contains("Model", StringComparison.Ordinal)); // the model is optional in a profile
    }

    [Fact]
    public void AHostOutsideTheProfilesAllowList_IsRefused_WithoutNamingTheHost()
    {
        using var provider = TestServices.Build("Production", Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:ActiveProfile", "lumindtr"),
            ("Llm:Profiles:lumindtr:BaseUrl", "https://abroad.example.test/llm/v1"),
            ("Llm:Profiles:lumindtr:AllowedHosts:0", "lumindtr.com"),
            ("Llm:Profiles:lumindtr:AuthScheme", "None")));

        var problems = OptionsValidator.ValidateLlm(provider.GetRequiredService<LlmOptions>(), development: false);
        Assert.Contains(problems, p => p.Contains("Llm:Profiles:lumindtr:BaseUrl", StringComparison.Ordinal) && p.Contains("Llm:Profiles:lumindtr:AllowedHosts", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain("abroad.example.test", p, StringComparison.Ordinal));

        // and the application cannot even build the responder
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChatResponder>());
        Assert.Contains("Llm:Profiles:lumindtr:BaseUrl", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abroad.example.test", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthSchemeNone_NeverWorksOverPlainHttp_EvenForLocalhostInDevelopment(bool development)
    {
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            ActiveProfile = "yerel",
            Profiles = new Dictionary<string, LlmProfileOptions>
            {
                ["yerel"] = new() { BaseUrl = "http://localhost:8080/v1", AllowedHosts = ["localhost"], AuthScheme = "None" },
            },
        };

        Assert.Contains(OptionsValidator.ValidateLlm(options, development), p => p.Contains("https", StringComparison.Ordinal));

        // for contrast: with a key, plain http to localhost is still allowed in Development (unchanged rule)
        options.Profiles["yerel"].AuthScheme = null;
        options.Profiles["yerel"].ApiKey = ProfileKey;
        Assert.Equal(development, OptionsValidator.ValidateLlm(options, development).Count == 0);
    }

    [Fact]
    public void AuthSchemeNone_WithAKey_IsRefused_BecauseTheKeyWouldNeverBeSent()
    {
        using var provider = TestServices.Build("Production", Settings([.. LumindProfile(), ("Llm:Provider", "OpenAiCompatible"), ("Llm:Profiles:lumindtr:ApiKey", ProfileKey)]));

        var problems = OptionsValidator.ValidateLlm(provider.GetRequiredService<LlmOptions>(), development: false);

        Assert.Contains(problems, p => p.Contains("Llm:Profiles:lumindtr:ApiKey", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain(ProfileKey, p, StringComparison.Ordinal));
    }

    [Fact]
    public void ABadSchemeOrModelInAProfile_IsRefused_ByTheProfilesSettingName()
    {
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            ActiveProfile = "p",
            Profiles = new Dictionary<string, LlmProfileOptions>
            {
                ["p"] = new() { BaseUrl = "https://p.example.test/v1", AllowedHosts = ["p.example.test"], AuthScheme = "Basic", ApiKey = ProfileKey, Model = "bad model!" },
            },
        };

        var problems = OptionsValidator.ValidateLlm(options, development: false);

        Assert.Contains(problems, p => p.Contains("Llm:Profiles:p:AuthScheme", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Llm:Profiles:p:Model", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain(ProfileKey, p, StringComparison.Ordinal));
    }

    [Fact]
    public void AnInactiveProfile_IsNotChecked_SoAHalfWrittenOneCanWait()
    {
        var options = new LlmOptions
        {
            Provider = "OpenAiCompatible",
            BaseUrl = "https://llm.example.test/v1",
            AllowedHosts = ["llm.example.test"],
            ApiKey = FlatKey,
            Model = "m",
            Profiles = new Dictionary<string, LlmProfileOptions> { ["yarim"] = new() { BaseUrl = "not a url" } },
        };

        Assert.Empty(OptionsValidator.ValidateLlm(options, development: false));
    }

    [Fact]
    public void WithTheFakeProvider_AValidActiveProfile_ChangesNothing()
    {
        using var provider = TestServices.Build("Production", Settings(LumindProfile()));

        Assert.IsType<FakeChatResponder>(provider.GetRequiredService<IChatResponder>());
        Assert.Empty(OptionsValidator.ValidateLlm(provider.GetRequiredService<LlmOptions>(), development: false));
    }

    // ---------------------------------------------------------------- the key never leaks (a profile with a key)

    [Fact]
    public async Task AFailingProfileCall_LeaksNeitherTheKeyNorTheAddressNorTheBody()
    {
        var handler = new FakeLlmHandler((_, _) => Task.FromResult(FakeLlmHandler.Status(HttpStatusCode.InternalServerError)));
        var (responder, log, _) = Make(handler, Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:ActiveProfile", "other-1"),
            ("Llm:Profiles:other-1:BaseUrl", "https://other.example.test/v1"),
            ("Llm:Profiles:other-1:AllowedHosts:0", "other.example.test"),
            ("Llm:Profiles:other-1:ApiKey", ProfileKey),
            ("Llm:RetryBaseDelayMs", "1")));

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => Read(responder));

        var everything = ex + string.Join("\n", log.Lines);
        Assert.DoesNotContain(ProfileKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("other.example.test", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-BODY", everything, StringComparison.Ordinal);
        // the key travelled in the Authorization header and nowhere else
        Assert.All(handler.Requests, r =>
        {
            Assert.DoesNotContain(ProfileKey, r.Uri!.ToString() + r.Body, StringComparison.Ordinal);
            Assert.Equal(["Authorization"], r.Headers.Where(h => h.Value.Contains(ProfileKey, StringComparison.Ordinal)).Select(h => h.Key).ToArray());
        });
    }

    // ---------------------------------------------------------------- the startup warning for an open endpoint

    [Fact]
    public async Task AnActiveProfileWithoutAuthentication_LogsOneWarning_WithoutAddressOrContent()
    {
        using var provider = TestServices.Build("Development", WithAdmin(Settings([.. LumindProfile(), ("Llm:Provider", "OpenAiCompatible")])));
        var log = new ListLogger<ConfigurationValidationService>();
        var service = ActivatorUtilities.CreateInstance<ConfigurationValidationService>(provider, log);

        await service.StartAsync(default);

        var line = Assert.Single(log.Lines);
        Assert.StartsWith("Warning: An authentication-free model profile is active", line, StringComparison.Ordinal);
        Assert.DoesNotContain("lumindtr.com", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthenticatedSetup_LogsNoSuchWarning()
    {
        using var provider = TestServices.Build("Development", WithAdmin(Settings(Evren())));
        var log = new ListLogger<ConfigurationValidationService>();
        var service = ActivatorUtilities.CreateInstance<ConfigurationValidationService>(provider, log);

        await service.StartAsync(default);

        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task ABadProfile_StopsTheStartup_NamingTheSetting()
    {
        using var provider = TestServices.Build("Development", WithAdmin(Settings(
            ("Llm:Provider", "OpenAiCompatible"),
            ("Llm:ActiveProfile", "lumindtr")))); // no Llm:Profiles:lumindtr:* at all
        var service = ActivatorUtilities.CreateInstance<ConfigurationValidationService>(provider, new ListLogger<ConfigurationValidationService>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Llm:ActiveProfile", ex.Message, StringComparison.Ordinal);
    }
}
