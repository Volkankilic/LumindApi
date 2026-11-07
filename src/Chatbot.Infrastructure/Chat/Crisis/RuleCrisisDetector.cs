using Chatbot.Application.Chat;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Chat.Crisis;

/// <summary>
/// The real, rule based crisis detector (ADR 008, docs/kriz/kriz-algilama.md). Deterministic, in memory, no I/O, no
/// external service. It looks at two views of the normalised message (punctuation separating words, and punctuation
/// removed) and reports the first phrase that matches. There is NO negation suppression in this version: "intihar
/// etmeyi düşünmüyorum" also raises the flag, because recall comes first and a wrong "safe" is the costliest error.
/// It never logs or stores the message, the phrase or the category.
/// </summary>
// TODO(volkan): klinik onay: the phrase list is a draft until a clinical expert has reviewed it.
public sealed class RuleCrisisDetector : ICrisisDetector
{
    private readonly CrisisLexicon _lexicon;

    public RuleCrisisDetector(CrisisLexicon lexicon, ILogger<RuleCrisisDetector>? logger = null)
    {
        _lexicon = lexicon;

        // The version and the size only, never a phrase.
        logger?.LogInformation(
            "Crisis detector ready: phrase list version {Version}, {Count} phrases.", lexicon.Version, lexicon.PhraseCount);
    }

    public static RuleCrisisDetector CreateDefault(ILogger<RuleCrisisDetector>? logger = null) =>
        new(CrisisLexicon.LoadEmbedded(), logger);

    public string Version => _lexicon.Version;

    public int PhraseCount => _lexicon.PhraseCount;

    public bool IsProductionGrade => true;

    public CrisisAssessment Assess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CrisisAssessment.None;
        }

        var separated = CrisisText.Tokenize(text, punctuationJoins: false);
        var category = _lexicon.Match(separated.Tokens, separated.Runs);
        if (category == CrisisCategory.None)
        {
            var joined = CrisisText.Tokenize(text, punctuationJoins: true);
            category = _lexicon.Match(joined.Tokens, joined.Runs);
        }

        return category == CrisisCategory.None ? CrisisAssessment.None : CrisisAssessment.Of(category);
    }
}

/// <summary>Any detector that fires makes it a crisis (Development: the real one next to the keyword stand-in).</summary>
public sealed class CompositeCrisisDetector(params ICrisisDetector[] detectors) : ICrisisDetector
{
    public bool IsProductionGrade => detectors.Any(d => d.IsProductionGrade);

    public CrisisAssessment Assess(string text)
    {
        foreach (var detector in detectors)
        {
            var assessment = detector.Assess(text);
            if (assessment.IsCrisis)
            {
                return assessment;
            }
        }

        return CrisisAssessment.None;
    }
}

/// <summary>
/// Fail safe. If the detector throws, the message counts as a crisis and the safe flow runs. The failure is logged by
/// exception TYPE only: never the message, never a phrase.
/// </summary>
public sealed class FailSafeCrisisDetector(ICrisisDetector inner, ILogger<FailSafeCrisisDetector> logger) : ICrisisDetector
{
    public ICrisisDetector Inner => inner;

    public bool IsProductionGrade => inner.IsProductionGrade;

    public CrisisAssessment Assess(string text)
    {
        try
        {
            return inner.Assess(text);
        }
        catch (Exception ex)
        {
            logger.LogError("Crisis detection failed with an exception of type {ExceptionType}; treating the message as a crisis.", ex.GetType().FullName);
            return CrisisAssessment.Unavailable;
        }
    }
}
