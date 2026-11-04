using Chatbot.Application.Options;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chatbot.Tests.TestSupport;

/// <summary>
/// Fixed keys for tests ONLY (never the real ones, which live in user-secrets / Railway variables). The tests that write
/// to the shared database therefore leave nothing a real key could read, and they delete their rows at the end.
/// </summary>
public static class TestEncryption
{
    public const string KeyId = "test";

    /// <summary>base64 of 32 bytes (all 0x07).</summary>
    public static readonly string KeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

    public const string OtherKeyId = "test2";

    public static readonly string OtherKeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

    public static PrivacyOptions Privacy(Action<PrivacyOptions>? change = null)
    {
        var options = new PrivacyOptions
        {
            IpHashKey = new string('i', 48),
            CurrentDataEncryptionKeyId = KeyId,
        };
        options.DataEncryptionKeys[KeyId] = KeyBase64;
        change?.Invoke(options);
        return options;
    }

    public static AesGcmFieldProtector Protector(PrivacyOptions? privacy = null) => new(privacy ?? Privacy());

    /// <summary>One shared instance: EF builds (and caches) an internal service provider per distinct set of options, so a new interceptor for every context would exhaust that cache.</summary>
    public static readonly FieldEncryptionInterceptor Shared = new(Protector(), NullLogger<FieldEncryptionInterceptor>.Instance);

    public static FieldEncryptionInterceptor Interceptor(AesGcmFieldProtector? protector = null) =>
        new(protector ?? Protector(), NullLogger<FieldEncryptionInterceptor>.Instance);

    // Contexts with different options do not share an in-memory database by name alone: they share a ROOT.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InMemoryDatabaseRoot> Roots = new();

    private static InMemoryDatabaseRoot RootOf(string name) => Roots.GetOrAdd(name, _ => new InMemoryDatabaseRoot());

    /// <summary>An in-memory context with the same interceptor the application uses (no database, no network).</summary>
    public static AppDbContext InMemoryContext(string? name = null, FieldEncryptionInterceptor? interceptor = null)
    {
        name ??= Guid.NewGuid().ToString("N");
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name, RootOf(name))
            .AddInterceptors(interceptor ?? Shared)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)) // one in-memory root per test
            .Options);
    }

    /// <summary>The SAME in-memory database WITHOUT the interceptor: it sees (and writes) exactly what is stored.</summary>
    public static AppDbContext RawInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name, RootOf(name))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options);
}
