using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Application.Conversations;
using Chatbot.Application.Options;
using Chatbot.Infrastructure.Chat;
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
        }

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(sp => Bind<AuthOptions>(sp, AuthOptions.Section));
        services.AddSingleton(sp => Bind<DocumentsOptions>(sp, DocumentsOptions.Section));
        services.AddSingleton(sp => Bind<PrivacyOptions>(sp, PrivacyOptions.Section));
        services.AddSingleton(sp => Bind<ChatOptions>(sp, ChatOptions.Section));
        services.AddSingleton(sp => Bind<DatabaseOptions>(sp, DatabaseOptions.Section));

        // EF Core is only the ORM: no Migrate(), no EnsureCreated() anywhere. The schema comes from api/db/*.sql.
        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

        services.AddSingleton<ISecretHasher, SecretHasher>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IParticipantAccess, ParticipantAccess>();
        services.AddScoped<IParticipantService, ParticipantService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IChatStore, ChatStore>();
        services.AddScoped<ISchemaStatusProvider, SchemaStatusProvider>();

        services.AddSingleton<ConversationLocks>();
        services.AddScoped<IChatService, ChatService>();
        services.AddSingleton<IPiiRedactor, NoopPiiRedactor>();
        services.AddSingleton<ICrisisResponseProvider, CrisisResponseProvider>();

        // Real crisis detection arrives in phase 5. Until then: nothing is flagged, except the clearly marked
        // keyword stand-in that exists in Development only.
        services.AddSingleton<ICrisisDetector>(sp =>
            sp.GetRequiredService<IHostEnvironment>().IsDevelopment() ? new DevKeywordCrisisDetector() : new NoopCrisisDetector());

        // Development-only trigger words (/hata, /limit, /yarim, /yavas, /kisa).
        services.AddSingleton<DevChatTriggers>();
        services.AddSingleton<IDevChatTriggers>(sp =>
            sp.GetRequiredService<IHostEnvironment>().IsDevelopment()
                ? sp.GetRequiredService<DevChatTriggers>()
                : new NoopDevChatTriggers());

        services.AddSingleton<IChatResponder>(sp =>
        {
            IChatResponder responder = new FakeChatResponder(sp.GetRequiredService<ChatOptions>());
            return sp.GetRequiredService<IHostEnvironment>().IsDevelopment()
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
