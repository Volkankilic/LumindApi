using Chatbot.Application.Chat;

namespace Chatbot.Infrastructure.Chat;

/// <summary>The default detector: flags nothing. Real, rule based detection comes in phase 5 with the clinical team.</summary>
public sealed class NoopCrisisDetector : ICrisisDetector
{
    public bool IsCrisis(string text) => false;
}

/// <summary>
/// WARNING: this is NOT real crisis detection. It recognises a few phrases only so the crisis flow (flag, "crisis" event,
/// safe reply, emergency card in the client) can be tried in Development. It is registered in Development ONLY.
/// Real detection (rule based, built with the clinical team, independent of any AI) arrives in phase 5.
/// </summary>
// TODO(volkan): replace with the clinically approved, rule based detector (phase 5).
public sealed class DevKeywordCrisisDetector : ICrisisDetector
{
    // The same phrases as the client mock (MOCK_CRISIS_PHRASES).
    private static readonly string[] Phrases =
    [
        "kendime zarar",
        "intihar",
        "yaşamak istemiyorum",
        "ölmek istiyorum",
    ];

    public bool IsCrisis(string text)
    {
        var normalized = TurkishText.Lower(text);
        return Phrases.Any(p => normalized.Contains(p, StringComparison.Ordinal));
    }
}

/// <summary>
/// The crisis protocol of docs/system-prompt.md, section 8 (empathy, safety, professional support, 112), using the
/// example text given there (the same text as the client mock).
/// </summary>
// TODO(volkan): clinical team to approve the final crisis reply.
public sealed class CrisisResponseProvider : ICrisisResponseProvider
{
    public const string Reply =
        "Bu duyguların ağırlığını hissediyorum. Güvende olman çok önemli. Kendine zarar verme düşüncelerin varsa lütfen en yakın sağlık profesyoneline başvur ya da 112’yi ara.";

    public string GetReply() => Reply;
}

/// <summary>Phase 4 puts the PII masking service here. For now text passes through unchanged.</summary>
public sealed class NoopPiiRedactor : IPiiRedactor
{
    public string Redact(string text) => text;
}
