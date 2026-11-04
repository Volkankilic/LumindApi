using Chatbot.Domain;
using Chatbot.Domain.Rules;

namespace Chatbot.Api.Controllers;

// Wire models. JSON is camelCase and enums are camelCase strings (see ADR 004).

public sealed record CodeRequest(string? Code);

/// <summary>Body of code sign-in and refresh. The refresh token is never in the body: it travels only as an HttpOnly cookie.</summary>
public sealed record AuthResponse(string AccessToken, int ExpiresInSeconds, ParticipantState State);

public sealed record ConsentRecordRequest(ConsentType Type, string? Version, bool Granted);

public sealed record ConsentRequest(List<ConsentRecordRequest>? Records);

public sealed record NicknameRequest(string? Nickname);

public sealed record SendMessageRequest(string? Text, Guid ClientMessageId);

/// <summary>`GET /api/health`: only ready / not ready. The schema detail is shown in Development only.</summary>
public sealed record HealthResponse(
    string Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SchemaDetail? Schema = null);

public sealed record SchemaDetail(int Expected, int? Found, string? Message);
