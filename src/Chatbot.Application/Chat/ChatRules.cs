using System.Text.RegularExpressions;
using Chatbot.Domain;
using Chatbot.Domain.Entities;

namespace Chatbot.Application.Chat;

/// <summary>What to do with a request whose client message id may have been seen before.</summary>
public enum IdempotencyDecision
{
    /// <summary>Not seen: store the user message and produce a reply.</summary>
    Process,

    /// <summary>Seen and answered completely: play the stored reply again, produce nothing.</summary>
    Replay,

    /// <summary>Seen, but the reply is missing, stopped or incomplete: drop the old reply and produce it again.</summary>
    Regenerate,
}

public static partial class ChatRules
{
    public static IdempotencyDecision Decide(bool userMessageExisted, MessageCompletion? replyCompletion, bool hasReply)
    {
        if (!userMessageExisted)
        {
            return IdempotencyDecision.Process;
        }

        return hasReply && replyCompletion == MessageCompletion.Complete
            ? IdempotencyDecision.Replay
            : IdempotencyDecision.Regenerate;
    }

    /// <summary>Returns the cleaned text. Throws empty-message / too-long (422) exactly like the contract says.</summary>
    public static string ValidateText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw AppException.EmptyMessage();
        }

        if (text.Length > maxLength)
        {
            throw AppException.TooLong();
        }

        return text.Trim();
    }

    /// <summary>
    /// The latest <paramref name="take"/> messages. Only COMPLETE assistant messages and all user messages are context:
    /// a stopped or cut off reply is not something the participant (or a model) should build on.
    /// </summary>
    public static IReadOnlyList<ContextMessage> BuildContext(IEnumerable<Message> messagesOldestFirst, int take) =>
        messagesOldestFirst
            .Where(m => m.Role == MessageRole.User || m.Completion == MessageCompletion.Complete)
            .TakeLast(Math.Max(1, take))
            .Select(m => new ContextMessage(m.Role, m.Content))
            .ToList();

    /// <summary>Splits a reply into words that keep their trailing space, as the client mock did.</summary>
    public static IReadOnlyList<string> Chunks(string text)
    {
        var matches = WordWithTrailingSpace().Matches(text);
        if (matches.Count == 0)
        {
            return [text];
        }

        return matches.Select(m => m.Value).ToList();
    }

    [GeneratedRegex(@"\S+\s*")]
    private static partial Regex WordWithTrailingSpace();
}
