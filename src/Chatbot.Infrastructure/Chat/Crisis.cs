using Chatbot.Application.Chat;

namespace Chatbot.Infrastructure.Chat;

/// <summary>
/// Flags nothing. It exists for tests and as the thing Production must NEVER run with: the startup check refuses to
/// start Production unless a production-grade detector (<see cref="Crisis.RuleCrisisDetector"/>) is registered.
/// </summary>
public sealed class NoopCrisisDetector : ICrisisDetector
{
    public CrisisAssessment Assess(string text) => CrisisAssessment.None;
}

/// <summary>
/// WARNING: this is NOT real crisis detection. It recognises a few phrases only so the crisis flow (flag, "crisis" event,
/// safe reply, emergency card in the client) can be tried in Development. It is registered in Development ONLY, next to
/// the real <see cref="Crisis.RuleCrisisDetector"/> (either one can raise the flag); the e2e tests rely on these phrases.
/// </summary>
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

    public CrisisAssessment Assess(string text)
    {
        var normalized = TurkishText.Lower(text);
        return Phrases.Any(p => normalized.Contains(p, StringComparison.Ordinal))
            ? CrisisAssessment.Of(CrisisCategory.Unknown)
            : CrisisAssessment.None;
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

    // TODO(volkan): clinical approval: whether a category (e.g. a medical emergency) needs its own text. Today every category gets this reply.
    public string GetReply(CrisisAssessment assessment) => Reply;
}

/// <summary>Phase 4 puts the PII masking service here. For now text passes through unchanged.</summary>
public sealed class NoopPiiRedactor : IPiiRedactor
{
    public string Redact(string text) => text;
}
