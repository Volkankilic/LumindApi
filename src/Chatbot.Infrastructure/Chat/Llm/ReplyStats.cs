namespace Chatbot.Infrastructure.Chat.Llm;

/// <summary>
/// Numbers about one model reply, for the evaluation command. Only counts: the text of the reasoning is never kept (it is measured
/// where it is read and dropped), and nothing here is a reply, a message or a key.
/// </summary>
public sealed class ReplyStats
{
    /// <summary>The server's own count (<c>usage.completion_tokens</c>), or null when it did not send one.</summary>
    public int? CompletionTokens { get; internal set; }

    /// <summary>Characters of "reasoning" / "reasoning_content" the server streamed. The content itself is NOT stored.</summary>
    public long ReasoningChars { get; internal set; }

    /// <summary>The <c>model</c> field of the reply, when the server sent one that looks like a model name. Information for the report only.</summary>
    public string? ServerModel { get; internal set; }

    internal void Reset()
    {
        ServerModel = null;
        CompletionTokens = null;
        ReasoningChars = 0;
    }
}
