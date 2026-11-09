using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Application.Conversations;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Chat;
using Chatbot.Infrastructure.Chat.Crisis;
using Chatbot.Infrastructure.Chat.Llm;
using Chatbot.Infrastructure.Conversations;
using Chatbot.Infrastructure.Dev;
using Chatbot.Infrastructure.Participants;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Security;
using Chatbot.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Everything is resolved lazily from IConfiguration / IHostEnvironment at runtime (not while registering), so the
    /// environment and configuration of a test host apply too. Secrets come only from user-secrets / environment variables.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IHostEnvironment? environment = null)
    {
        // The test participant service (it deletes consent and audit rows) exists in Development ONLY: in any other
        // environment it is not even registered.
        if (environment?.IsDevelopment() == true)
        {
            services.AddScoped<DevTestParticipantService>();
            services.AddScoped<DevTestAdminService>();
        }

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(sp => Bind<AuthOptions>(sp, AuthOptions.Section));
        services.AddSingleton(sp => Bind<AdminAuthOptions>(sp, AdminAuthOptions.Section));
        services.AddSingleton(sp => Bind<DocumentsOptions>(sp, DocumentsOptions.Section));
        services.AddSingleton(sp => Bind<PrivacyOptions>(sp, PrivacyOptions.Section));
        services.AddSingleton(sp => Bind<ChatOptions>(sp, ChatOptions.Section));
        services.AddSingleton(sp => Bind<LlmOptions>(sp, LlmOptions.Section));
        services.AddSingleton(sp => Bind<DatabaseOptions>(sp, DatabaseOptions.Section));

        // EF Core is only the ORM: no Migrate(), no EnsureCreated() anywhere. The schema comes from api/db/*.sql.
        // Field encryption (message content, nickname) and audit protection live in one interceptor (ADR 007).
        services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();
        services.AddSingleton<FieldEncryptionInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) =>
            options
                .UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default"))
                .AddInterceptors(sp.GetRequiredService<FieldEncryptionInterceptor>()));

        services.AddSingleton<ISecretHasher, SecretHasher>();
        services.AddSingleton<AuthFailureBudget>();
        services.AddScoped<Chatbot.Infrastructure.Retention.RetentionService>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuthService, AuthService>();

        // Admin panel (phase 7, ADR 009).
        services.AddSingleton(sp => new Chatbot.Infrastructure.Admin.AdminPasswordHasher(sp.GetRequiredService<AdminAuthOptions>().Pbkdf2Iterations));
        services.AddSingleton<Chatbot.Infrastructure.Admin.AdminLoginTracker>();
        services.AddScoped<Chatbot.Application.Admin.IAdminAuthService, Chatbot.Infrastructure.Admin.AdminAuthService>();
        services.AddScoped<Chatbot.Application.Admin.IAdminAccess, Chatbot.Infrastructure.Admin.AdminAccess>();
        services.AddScoped<Chatbot.Application.Admin.IAdminParticipantService, Chatbot.Infrastructure.Admin.AdminParticipantService>();
        services.AddScoped<Chatbot.Application.Admin.IAdminStatsService, Chatbot.Infrastructure.Admin.AdminStatsService>();
        services.AddScoped<Chatbot.Application.Admin.IAdminCrisisService, Chatbot.Infrastructure.Admin.AdminCrisisService>();
        services.AddScoped<Chatbot.Infrastructure.Admin.AdminAccountService>();
        services.AddScoped<IParticipantAccess, ParticipantAccess>();
        services.AddScoped<IParticipantService, ParticipantService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IChatStore, ChatStore>();
        services.AddScoped<ISchemaStatusProvider, SchemaStatusProvider>();

        services.AddSingleton<ConversationLocks>();
        services.AddScoped<IChatService, ChatService>();
        // Rule-based masking for text bound for a model provider (phase 6 connects it; no model exists today).
        services.AddSingleton<IPiiRedactor, Chatbot.Infrastructure.Privacy.RulePiiRedactor>();
        services.AddSingleton<ICrisisResponseProvider, CrisisResponseProvider>();

        // Crisis detection (phase 5, ADR 008): the rule based detector in EVERY environment, wrapped so that a failure
        // counts as a crisis. Development adds the keyword stand-in next to it (the e2e tests use its phrases).
        // Production refuses to start without a production-grade detector (ConfigurationValidationService).
        services.AddSingleton<ICrisisDetector>(sp =>
        {
            var rules = RuleCrisisDetector.CreateDefault(sp.GetRequiredService<ILogger<RuleCrisisDetector>>());
            ICrisisDetector inner = sp.GetRequiredService<IHostEnvironment>().IsDevelopment()
                ? new CompositeCrisisDetector(rules, new DevKeywordCrisisDetector())
                : rules;
            return new FailSafeCrisisDetector(inner, sp.GetRequiredService<ILogger<FailSafeCrisisDetector>>());
        });

        // Development-only trigger words (/hata, /limit, /yarim, /yavas, /kisa).
        services.AddSingleton<DevChatTriggers>();
        services.AddSingleton<IDevChatTriggers>(sp =>
            sp.GetRequiredService<IHostEnvironment>().IsDevelopment()
                ? sp.GetRequiredService<DevChatTriggers>()
                : new NoopDevChatTriggers());

        // The reply source. The canned fake reply is the default; the OpenAI compatible model client is opt-in through
        // Llm:Provider (phase 6a, ADR 012) and only starts when the base URL host is on Llm:AllowedHosts.
        services.AddSingleton<ISystemPromptProvider>(_ => SystemPromptText.LoadEmbedded());
        services.AddSingleton<IChatResponder>(sp =>
        {
            var development = sp.GetRequiredService<IHostEnvironment>().IsDevelopment();
            var llm = sp.GetRequiredService<LlmOptions>();
            IChatResponder responder = llm.IsReal
                ? new OpenAiCompatibleChatResponder(
                    LlmHttpClient.Create(),
                    llm,
                    sp.GetRequiredService<ISystemPromptProvider>(),
                    sp.GetRequiredService<ILogger<OpenAiCompatibleChatResponder>>(),
                    development)
                : new FakeChatResponder(sp.GetRequiredService<ChatOptions>());
            return development
                ? new DevTriggerChatResponder(responder, sp.GetRequiredService<DevChatTriggers>())
                : responder;
        });

        services.AddSingleton<SeedSqlGenerator>();
        return services;
    }

    private static T Bind<T>(IServiceProvider sp, string section)
        where T : new() =>
        sp.GetRequiredService<IConfiguration>().GetSection(section).Get<T>() ?? new T();
}
