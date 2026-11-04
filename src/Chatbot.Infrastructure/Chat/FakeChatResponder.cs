using System.Runtime.CompilerServices;
using Chatbot.Application.Chat;
using Chatbot.Application.Options;
using Chatbot.Domain;

namespace Chatbot.Infrastructure.Chat;

/// <summary>The fixed, empathic reply source of this phase. Streams a canned reply word by word.</summary>
public sealed class FakeChatResponder(ChatOptions options) : IChatResponder
{
    private const int FastFirstMs = 125;
    private const int FastTokenMs = 10;
    private const int SlowFirstMs = 8000;

    public async IAsyncEnumerable<string> StreamReplyAsync(
        ConversationContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lastUser = context.Messages.LastOrDefault(m => m.Role == MessageRole.User)?.Text ?? string.Empty;

        // How many general replies were given earlier in this conversation (decides which one comes next).
        var generalTexts = FakeReplies.GeneralTemplates.Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        var turn = context.Messages.Count(m => m.Role == MessageRole.Assistant && generalTexts.Contains(m.Text.Trim()));

        var reply = FakeReplies.Pick(lastUser, turn).Text;

        var (firstMs, tokenMs) = context.Pacing switch
        {
            ReplyPacing.Fast => (Math.Min(options.Fake.FirstTokenDelayMs, FastFirstMs), Math.Min(options.Fake.TokenDelayMs, FastTokenMs)),
            ReplyPacing.Slow => (options.Fake.FirstTokenDelayMs == 0 ? 0 : SlowFirstMs, options.Fake.TokenDelayMs),
            _ => (options.Fake.FirstTokenDelayMs, options.Fake.TokenDelayMs),
        };

        await Pause(firstMs, ct);
        var first = true;
        foreach (var chunk in ChatRules.Chunks(reply))
        {
            if (!first)
            {
                await Pause(tokenMs, ct);
            }

            first = false;
            ct.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    private static Task Pause(int ms, CancellationToken ct) => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);
}
