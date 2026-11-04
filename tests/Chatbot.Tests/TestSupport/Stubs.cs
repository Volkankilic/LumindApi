using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Chat;
using Chatbot.Application.Conversations;
using Chatbot.Domain.Rules;
using Serilog.Core;
using Serilog.Events;

namespace Chatbot.Tests.TestSupport;

// Stand-ins for the services behind the HTTP layer, so the controllers, authorization, rate limiting, error contract
// and SSE writer can be tested without a database.

public sealed class StubSchemaStatus : ISchemaStatusProvider
{
    public SchemaStatus Status { get; set; } = new(true, 1, 1, null);

    public Task<SchemaStatus> GetAsync(CancellationToken ct) => Task.FromResult(Status);
}

public sealed class StubParticipantAccess : IParticipantAccess
{
    public AccessStatus Status { get; set; } = AccessStatus.Ok;

    public Task<AccessStatus> CheckAsync(Guid participantId, CancellationToken ct) => Task.FromResult(Status);
}

public sealed class StubAuthService : IAuthService
{
    public static readonly Guid ParticipantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public Func<string?, AuthResult> SignIn { get; set; } = _ => throw AppException.InvalidCode();

    public Func<string?, AuthResult> Refresh { get; set; } = _ => throw AppException.Unauthorized();

    public List<string?> LoggedOut { get; } = [];

    /// <summary>The client address each sign-in call saw (after forwarded-header handling).</summary>
    public List<string?> ClientIps { get; } = [];

    public Task<AuthResult> SignInAsync(string? code, ClientInfo client, CancellationToken ct)
    {
        lock (ClientIps)
        {
            ClientIps.Add(client.Ip);
        }

        return Task.FromResult(SignIn(code));
    }

    public Task<AuthResult> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken ct) => Task.FromResult(Refresh(refreshToken));

    public Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct)
    {
        LoggedOut.Add(refreshToken);
        return Task.CompletedTask;
    }

    public static AuthResult Success() => new(
        "access-token-value",
        900,
        new ParticipantState(ParticipantId, null, null, null),
        new IssuedRefreshToken("refresh-token-value", DateTimeOffset.UtcNow.AddDays(7)));
}

public sealed class StubParticipantService : IParticipantService
{
    public ParticipantState State { get; set; } = new(StubAuthService.ParticipantId, null, null, null);

    public Task<ParticipantState> GetStateAsync(Guid participantId, ClientInfo client, CancellationToken ct) => Task.FromResult(State);

    public Task<ParticipantState> RecordConsentsAsync(Guid participantId, IReadOnlyList<ConsentInput> records, ClientInfo client, CancellationToken ct) =>
        Task.FromResult(State);

    public Task<ParticipantState> SetNicknameAsync(Guid participantId, string? nickname, ClientInfo client, CancellationToken ct) =>
        Task.FromResult(State);

    public Task DeleteAsync(Guid participantId, ClientInfo client, CancellationToken ct) => Task.CompletedTask;
}

public sealed class StubConversationService : IConversationService
{
    public Task<ConversationDto> StartAsync(Guid participantId, ClientInfo client, CancellationToken ct) =>
        Task.FromResult(new ConversationDto(Guid.NewGuid(), DateTimeOffset.UtcNow, null));

    public Task<IReadOnlyList<ConversationSummaryDto>> ListAsync(Guid participantId, ClientInfo client, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConversationSummaryDto>>([]);

    public Task<ConversationDetailDto> GetAsync(Guid participantId, Guid conversationId, ClientInfo client, CancellationToken ct) =>
        throw AppException.NotFound();

    public Task EndAsync(Guid participantId, Guid conversationId, ClientInfo client, CancellationToken ct) => throw AppException.NotFound();
}

public sealed class StubChatService : IChatService
{
    public Func<string?, IAsyncEnumerable<ChatEvent>> Handler { get; set; } = _ => Events();

    public IAsyncEnumerable<ChatEvent> SendAsync(Guid participantId, Guid conversationId, string? text, Guid clientMessageId, CancellationToken ct) =>
        Handler(text);

    public static async IAsyncEnumerable<ChatEvent> Events(params ChatEvent[] events)
    {
        foreach (var e in events)
        {
            yield return e;
        }

        await Task.CompletedTask;
    }
}

/// <summary>Collects every Serilog event so tests can prove what was (and was not) logged.</summary>
public sealed class CapturingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToList();
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>Message text plus every property value, as one string per event.</summary>
    public IEnumerable<string> Rendered() =>
        Events.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Key + "=" + p.Value)) + " " + e.Exception);
}
