using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Chatbot.Application;
using Chatbot.Application.Chat;
using Chatbot.Domain;

namespace Chatbot.Infrastructure.Dev;

// Development ONLY: trigger words for trying the client's error paths end to end (same meaning as the client mock).
// None of this is registered in any other environment (there is a test for it).

public enum DevTrigger { Error, Slow, Fast, Limit, Cutoff }

public static class DevTriggerWords
{
    private static readonly (string Word, DevTrigger Trigger)[] Words =
    [
        ("/hata", DevTrigger.Error),
        ("/yavas", DevTrigger.Slow),
        ("/kisa", DevTrigger.Fast),
        ("/limit", DevTrigger.Limit),
        ("/yarim", DevTrigger.Cutoff),
    ];

    public static DevTrigger? Detect(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var (word, trigger) in Words)
        {
            if (lower.Contains(word, StringComparison.Ordinal))
            {
                return trigger;
            }
        }

        return null;
    }
}

/// <summary>Remembers which triggers already failed once per client message id, so a retry with the same id succeeds.</summary>
public sealed class DevChatTriggers : IDevChatTriggers
{
    private readonly ConcurrentDictionary<(DevTrigger, Guid), byte> _failedOnce = new();

    public void BeforeAccept(string text, Guid clientMessageId)
    {
        switch (DevTriggerWords.Detect(text))
        {
            case DevTrigger.Error when FirstAttempt(DevTrigger.Error, clientMessageId):
                throw AppException.Unavailable();
            case DevTrigger.Limit when FirstAttempt(DevTrigger.Limit, clientMessageId):
                throw AppException.RateLimited(20);
        }
    }

    public bool FirstAttempt(DevTrigger trigger, Guid clientMessageId) => _failedOnce.TryAdd((trigger, clientMessageId), 0);
}

/// <summary>Production: no triggers.</summary>
public sealed class NoopDevChatTriggers : IDevChatTriggers
{
    public void BeforeAccept(string text, Guid clientMessageId)
    {
    }
}

/// <summary>Decorator (Development only): /yarim cuts the reply off, /yavas and /kisa change the pacing.</summary>
public sealed class DevTriggerChatResponder(IChatResponder inner, DevChatTriggers triggers) : IChatResponder
{
    private const int CutAfterChunks = 6;

    public string? PromptVersion => inner.PromptVersion;

    public async IAsyncEnumerable<string> StreamReplyAsync(
        ConversationContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lastUser = context.Messages.LastOrDefault(m => m.Role == MessageRole.User)?.Text ?? string.Empty;
        var trigger = DevTriggerWords.Detect(lastUser);

        var pacing = trigger switch
        {
            DevTrigger.Slow => ReplyPacing.Slow,
            DevTrigger.Fast => ReplyPacing.Fast,
            _ => context.Pacing,
        };
        var cut = trigger == DevTrigger.Cutoff && triggers.FirstAttempt(DevTrigger.Cutoff, context.ClientMessageId);

        var emitted = 0;
        await foreach (var chunk in inner.StreamReplyAsync(context with { Pacing = pacing }, ct))
        {
            if (cut && emitted == CutAfterChunks)
            {
                throw AppException.Unavailable();
            }

            emitted++;
            yield return chunk;
        }
    }
}
