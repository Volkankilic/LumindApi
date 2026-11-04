using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Application.Conversations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Chatbot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;

namespace Chatbot.Tests.TestSupport;

/// <summary>
/// TestServer has no real peer address. A test sets one with the X-Test-Peer header; it is applied BEFORE the
/// application's own pipeline, exactly like the socket address of a real connection would be.
/// </summary>
internal sealed class TestPeerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer)
                && System.Net.IPAddress.TryParse(peer.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await nextMiddleware();
        });
        next(app);
    };
}

/// <summary>
/// Hosts the real API pipeline with in-memory settings and (by default) stubbed services behind it: no database,
/// no user-secrets. The environment is "Testing" unless told otherwise, i.e. production-like (no dev-only parts).
/// </summary>
public sealed class ApiFactory(
    string environment = "Testing",
    Dictionary<string, string?>? settings = null,
    bool stubServices = true,
    bool realDatabase = false,
    Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
{
    // Fake secrets for tests only (long enough to pass validation). Real ones live in user-secrets.
    public static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["Auth:JwtSigningKey"] = new string('j', 48),
        ["Auth:CodePepper"] = new string('p', 48),
        ["Privacy:IpHashKey"] = new string('i', 48),
        [$"Privacy:DataEncryptionKeys:{TestEncryption.KeyId}"] = TestEncryption.KeyBase64,
        ["Privacy:CurrentDataEncryptionKeyId"] = TestEncryption.KeyId,
        ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=none;Timeout=1",
        ["Chat:Fake:FirstTokenDelayMs"] = "0",
        ["Chat:Fake:TokenDelayMs"] = "0",
    };

    public StubSchemaStatus Schema { get; } = new();

    public StubParticipantAccess Access { get; } = new();

    public StubAuthService Auth { get; } = new();

    public StubParticipantService Participants { get; } = new();

    public StubConversationService Conversations { get; } = new();

    public StubChatService Chat { get; } = new();

    public CapturingSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        var merged = DefaultSettings();
        if (realDatabase)
        {
            // Real database, test-only (fake) secrets: the test participants are created with this very pepper.
            // TestServer has no remote IP, so every call shares one rate limit key: the sign-in limit is lifted here
            // (its own test sets it back).
            merged["ConnectionStrings:Default"] = TestDatabase.ConnectionString;
            merged["Auth:CodeAttempts"] = "10000";
            merged["Auth:CookieSecure"] = "false";
        }

        foreach (var (key, value) in settings ?? [])
        {
            merged[key] = value;
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(merged));
        builder.ConfigureServices(services =>
        {
            // Every test host has its own interceptor instance, so EF builds one internal service provider per host. A test run
            // creates far more than twenty hosts and EF turns that warning into an exception (it stopped hosts at random). The
            // application itself runs ONE host, where this cannot happen.
            services.ConfigureDbContext<AppDbContext>(options => options.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddSingleton<ILogEventSink>(Logs);
            services.AddSingleton<IStartupFilter, TestPeerStartupFilter>();
            if (!stubServices)
            {
                configure?.Invoke(services);
                return;
            }

            services.Replace(ServiceDescriptor.Singleton<ISchemaStatusProvider>(Schema));
            services.Replace(ServiceDescriptor.Singleton<IParticipantAccess>(Access));
            services.Replace(ServiceDescriptor.Singleton<IAuthService>(Auth));
            services.Replace(ServiceDescriptor.Singleton<IParticipantService>(Participants));
            services.Replace(ServiceDescriptor.Singleton<IConversationService>(Conversations));
            services.Replace(ServiceDescriptor.Singleton<IChatService>(Chat));
        });
    }
}
