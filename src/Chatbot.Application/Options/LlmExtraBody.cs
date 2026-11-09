using System.Text;
using System.Text.Json;

namespace Chatbot.Application.Options;

/// <summary>
/// Extra request fields a provider or a model needs (e.g. <c>{"chat_template_kwargs":{"enable_thinking":false}}</c>), taken from
/// <c>Llm:ExtraBodyJson</c> (or the evaluation command's <c>--extra-body</c> file). They are merged next to the main fields and never
/// compiled in: a server that rejects unknown fields needs a different object per model and provider.
///
/// The text must be a JSON OBJECT of at most <see cref="MaxBytes"/> bytes, and must not set a field the client sets itself, so a setting can
/// never replace the model, the messages, the stream flag, the reply length or the temperature. Problem messages name the SETTING only:
/// never a key, a value or a piece of the text.
/// </summary>
public static class LlmExtraBody
{
    public const int MaxBytes = 2048;

    private const int MaxDepth = 8;

    /// <summary>The fields the client owns (besides the configurable token field, which is added at check time).</summary>
    private static readonly string[] Reserved = ["model", "messages", "stream", "max_tokens", "temperature"];

    private static readonly IReadOnlyDictionary<string, JsonElement> None = new Dictionary<string, JsonElement>();

    /// <summary>The fields to merge (cloned, so they outlive the parsed document), or the problem. Empty text means "no extra fields".</summary>
    public static (IReadOnlyDictionary<string, JsonElement> Fields, string? Problem) Parse(string? json, string? maxTokensParameter, string setting)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (None, null);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return (None, $"{setting} is too large (at most {MaxBytes} bytes).");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (None, $"{setting} must be a JSON object.");
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Reserved.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(maxTokensParameter) && string.Equals(property.Name, maxTokensParameter, StringComparison.OrdinalIgnoreCase)))
                {
                    return (None, $"{setting} must not set a field the client sets itself (model, messages, stream, temperature, the token limit field).");
                }

                if (!fields.TryAdd(property.Name, property.Value.Clone()))
                {
                    return (None, $"{setting} must not repeat a key.");
                }
            }

            return (fields, null);
        }
        catch (JsonException)
        {
            // The exception text can quote the input: it is dropped on purpose.
            return (None, $"{setting} must be valid JSON.");
        }
    }

    public static IReadOnlyList<string> Check(string? json, string? maxTokensParameter, string setting) =>
        Parse(json, maxTokensParameter, setting).Problem is { } problem ? [problem] : [];
}
