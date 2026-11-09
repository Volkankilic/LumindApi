using System.Runtime.CompilerServices;
using System.Text;
using Chatbot.Application.Options;
using Chatbot.Domain;

namespace Chatbot.Application.Chat;

/// <summary>
/// The send pipeline. The order is deliberate and crisis detection comes BEFORE any model:
/// validate, store the user message (Accepted), detect a crisis, then stream the safe crisis reply or the responder's reply.
/// </summary>
public sealed class ChatService(
    IChatStore store,
    ICrisisDetector crisisDetector,
    ICrisisResponseProvider crisisReplies,
    IChatResponder responder,
    IPiiRedactor redactor,
    IDevChatTriggers triggers,
    ConversationLocks locks,
    ChatOptions options) : IChatService
{
    public async IAsyncEnumerable<ChatEvent> SendAsync(
        Guid participantId,
        Guid conversationId,
        string? text,
        Guid clientMessageId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // ---- Before "accepted": every failure is a plain HTTP error ("the message did not arrive").
        var clean = ChatRules.ValidateText(text, options.MaxMessageLength);
        var conversation = await store.FindOwnedConversationAsync(participantId, conversationId, ct)
            ?? throw AppException.NotFound();
        if (conversation.EndedAt is not null)
        {
            throw AppException.ConversationEnded();
        }

        triggers.BeforeAccept(clean, clientMessageId);

        using var turn = await locks.AcquireAsync(conversationId, ct);

        var (userMessage, existed) = await store.AddUserMessageAsync(conversationId, clientMessageId, clean, ct);
        var previousReply = existed ? await store.FindReplyAsync(userMessage, ct) : null;
        var decision = ChatRules.Decide(existed, previousReply?.Completion, previousReply is not null);

        yield return new ChatEvent.Accepted(userMessage.Id);

        // ---- After "accepted": failures are events inside the stream ("the reply was cut off").
        if (decision == IdempotencyDecision.Replay)
        {
            if (previousReply!.IsCrisis)
            {
                yield return new ChatEvent.Crisis();
            }

            foreach (var chunk in ChatRules.Chunks(previousReply.Content))
            {
                yield return new ChatEvent.Delta(chunk);
            }

            yield return new ChatEvent.Done(previousReply.Id);
            yield break;
        }

        if (previousReply is not null)
        {
            // A stopped or cut off reply of an earlier attempt: drop it, the reply is produced again.
            await store.DeleteMessageAsync(previousReply.Id, ct);
        }

        // Fail safe: if the detector itself breaks, the message counts as a crisis. (The registered detector already does
        // this and logs the failure by exception type; this is the last line of defence for any other implementation.)
        CrisisAssessment assessment;
        try
        {
            assessment = crisisDetector.Assess(clean);
        }
        catch (Exception)
        {
            assessment = CrisisAssessment.Unavailable;
        }

        var isCrisis = assessment.IsCrisis;
        if (isCrisis)
        {
            if (!userMessage.IsCrisis)
            {
                await store.MarkCrisisAsync(participantId, conversationId, userMessage.Id, ct);
            }

            yield return new ChatEvent.Crisis();
        }

        var written = new StringBuilder();
        var saved = false;

        // Which prompt produced this reply: the fixed crisis reply has none (the model is not involved), a fallback reply is marked below.
        var promptVersion = isCrisis ? null : responder.PromptVersion;
        try
        {
            IAsyncEnumerable<string> source = isCrisis
                ? AsAsync(ChatRules.Chunks(crisisReplies.GetReply(assessment)))
                : responder.StreamReplyAsync(await BuildContextAsync(conversationId, clientMessageId, ct), ct);

            await using var enumerator = source.GetAsyncEnumerator(ct);
            Exception? failure = null;
            var cancelled = false;
            while (true)
            {
                string chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    chunk = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                written.Append(chunk);
                yield return new ChatEvent.Delta(chunk);
            }

            if (cancelled)
            {
                yield break; // the finally block keeps what was written as "Stopped"
            }

            if (failure is ModelUnavailableException && written.Length == 0)
            {
                // The model could not answer and nothing was shown yet: the participant gets the fixed, kind fallback text as a normal
                // reply. It is marked in system_prompt_version so that exports can tell it from a model reply, and it is never context.
                promptVersion = FallbackReply.Version;
                foreach (var chunk in ChatRules.Chunks(FallbackReply.Text))
                {
                    written.Append(chunk);
                    yield return new ChatEvent.Delta(chunk);
                }

                var fallbackId = await store.AddAssistantMessageAsync(
                    conversationId, written.ToString().TrimEnd(), MessageCompletion.Complete, false, promptVersion, CancellationToken.None);
                saved = true;
                yield return new ChatEvent.Done(fallbackId);
                yield break;
            }

            if (failure is not null)
            {
                if (written.Length > 0)
                {
                    await store.AddAssistantMessageAsync(
                        conversationId, written.ToString().TrimEnd(), MessageCompletion.Incomplete, isCrisis, promptVersion, CancellationToken.None);
                }

                saved = true;
                yield return failure is AppException known
                    ? new ChatEvent.Error(known.Code, known.RetryAfterSeconds)
                    : new ChatEvent.Error("unknown");
                yield break;
            }

            var assistantId = await store.AddAssistantMessageAsync(
                conversationId, written.ToString().TrimEnd(), MessageCompletion.Complete, isCrisis, promptVersion, CancellationToken.None);
            saved = true;
            yield return new ChatEvent.Done(assistantId);
        }
        finally
        {
            // The client went away (or the request was cancelled) mid-reply: keep what the participant saw.
            if (!saved && written.Length > 0)
            {
                try
                {
                    await store.AddAssistantMessageAsync(
                        conversationId, written.ToString().TrimEnd(), MessageCompletion.Stopped, isCrisis, promptVersion, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Best effort: a failure here must not hide the original outcome.
                }
            }
        }
    }

    private async Task<ConversationContext> BuildContextAsync(Guid conversationId, Guid clientMessageId, CancellationToken ct)
    {
        // Read a little more than needed: incomplete replies are filtered out before the cut.
        var recent = await store.GetRecentAsync(conversationId, options.ContextMessages * 2, ct);
        var context = ChatRules.BuildModelContext(recent, options.ContextMessages, redactor);
        return new ConversationContext(context, clientMessageId);
    }

    private static async IAsyncEnumerable<string> AsAsync(IEnumerable<string> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }

        await Task.CompletedTask;
    }
}
