using System.Text.Json;
using Chatbot.Application.Chat;

namespace Chatbot.Infrastructure.Chat.Crisis;

/// <summary>One word position in a phrase: any of several words, each exact or a prefix ("istiyor*").</summary>
internal sealed class CrisisTerm(int id, string[] exact, string[] prefixes)
{
    public int Id { get; } = id;

    public bool Matches(string token)
    {
        foreach (var e in exact)
        {
            if (string.Equals(token, e, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var p in prefixes)
        {
            if (token.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class CrisisPhrase(string id, CrisisCategory category, CrisisTerm[] terms, int gap, string[] compactKeys)
{
    public string Id { get; } = id;

    public CrisisCategory Category { get; } = category;

    public CrisisTerm[] Terms { get; } = terms;

    /// <summary>The most words that may sit between two consecutive terms.</summary>
    public int Gap { get; } = gap;

    /// <summary>The phrase with the spaces removed, for text where the letters were spelled out one by one.</summary>
    public string[] CompactKeys { get; } = compactKeys;
}

/// <summary>What the review sheet needs from one phrase (readable pattern, explanation, risk).</summary>
public sealed record CrisisPhraseInfo(
    string Id,
    string CategoryId,
    string CategoryTitle,
    string Pattern,
    int Gap,
    string Example,
    string Reason,
    string Risk);

/// <summary>
/// The phrase list, compiled. The matcher is linear: tokens are compared with the terms, no regular expression is
/// involved, so there is no catastrophic backtracking by construction. Memory only, no I/O after loading.
/// </summary>
public sealed class CrisisLexicon
{
    public const string ResourceName = "Chatbot.Infrastructure.Chat.Crisis.crisis-phrases.tr.json";

    private const int MaxGap = 6;
    private const int MaxCompactKeysPerPhrase = 64;

    private readonly CrisisPhrase[] _phrases;
    private readonly int _termCount;

    private CrisisLexicon(string version, CrisisPhrase[] phrases, int termCount, IReadOnlyList<CrisisPhraseInfo> infos)
    {
        Version = version;
        _phrases = phrases;
        _termCount = termCount;
        Phrases = infos;
    }

    public string Version { get; }

    public int PhraseCount => _phrases.Length;

    public IReadOnlyList<CrisisPhraseInfo> Phrases { get; }

    /// <summary>The list that ships inside the assembly.</summary>
    public static CrisisLexicon LoadEmbedded()
    {
        using var stream = typeof(CrisisLexicon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The crisis phrase list is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Compiles a list. Problems name the phrase id only, never a phrase.</summary>
    public static CrisisLexicon Parse(string json)
    {
        var file = JsonSerializer.Deserialize<LexiconFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The crisis phrase list is empty.");
        if (string.IsNullOrWhiteSpace(file.Version))
        {
            throw new InvalidOperationException("The crisis phrase list has no version.");
        }

        var categories = new Dictionary<string, (CrisisCategory Category, string Title)>(StringComparer.Ordinal);
        foreach (var c in file.Categories ?? [])
        {
            if (!Enum.TryParse<CrisisCategory>(c.Name, out var category) || category is CrisisCategory.None or CrisisCategory.Unknown)
            {
                throw new InvalidOperationException($"Crisis category {c.Id} has an unknown name.");
            }

            categories[c.Id ?? string.Empty] = (category, c.Title ?? c.Name ?? string.Empty);
        }

        var terms = new Dictionary<string, CrisisTerm>(StringComparer.Ordinal);
        var phrases = new List<CrisisPhrase>();
        var infos = new List<CrisisPhraseInfo>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in file.Phrases ?? [])
        {
            var id = p.Id ?? string.Empty;
            if (id.Length == 0 || !ids.Add(id))
            {
                throw new InvalidOperationException("A crisis phrase has a missing or duplicate id.");
            }

            if (!categories.TryGetValue(p.Category ?? string.Empty, out var category))
            {
                throw new InvalidOperationException($"Crisis phrase {id} has an unknown category.");
            }

            if (p.Gap is < 0 or > MaxGap)
            {
                throw new InvalidOperationException($"Crisis phrase {id} has a gap outside 0..{MaxGap}.");
            }

            var parsed = ParsePattern(id, p.Pattern, terms);
            phrases.Add(new CrisisPhrase(id, category.Category, parsed.Terms, p.Gap, parsed.CompactKeys));
            infos.Add(new CrisisPhraseInfo(
                id, p.Category!, category.Title, Readable(p.Pattern!), p.Gap, p.Example ?? string.Empty, p.Reason ?? string.Empty, p.Risk ?? string.Empty));
        }

        if (phrases.Count == 0)
        {
            throw new InvalidOperationException("The crisis phrase list has no phrases.");
        }

        return new CrisisLexicon(file.Version.Trim(), [.. phrases], terms.Count, infos);
    }

    /// <summary>Which category the tokens fall into, or None. Never throws on any input.</summary>
    public CrisisCategory Match(IReadOnlyList<string> tokens, IReadOnlyList<string> spelledOutRuns)
    {
        if (tokens.Count > 0)
        {
            var positions = new int[]?[_termCount];
            foreach (var phrase in _phrases)
            {
                if (MatchPhrase(phrase, tokens, positions))
                {
                    return phrase.Category;
                }
            }
        }

        foreach (var run in spelledOutRuns)
        {
            foreach (var phrase in _phrases)
            {
                foreach (var key in phrase.CompactKeys)
                {
                    if (run.Contains(key, StringComparison.Ordinal))
                    {
                        return phrase.Category;
                    }
                }
            }
        }

        return CrisisCategory.None;
    }

    private static bool MatchPhrase(CrisisPhrase phrase, IReadOnlyList<string> tokens, int[]?[] cache)
    {
        var current = Positions(phrase.Terms[0], tokens, cache);
        for (var t = 1; t < phrase.Terms.Length && current.Length > 0; t++)
        {
            var next = Positions(phrase.Terms[t], tokens, cache);
            var kept = new List<int>();
            var j = 0;
            foreach (var p in next)
            {
                // an earlier term must sit in [p - gap - 1, p - 1]
                while (j < current.Length && current[j] < p - phrase.Gap - 1)
                {
                    j++;
                }

                if (j < current.Length && current[j] <= p - 1)
                {
                    kept.Add(p);
                }
            }

            current = [.. kept];
        }

        return current.Length > 0;
    }

    private static int[] Positions(CrisisTerm term, IReadOnlyList<string> tokens, int[]?[] cache)
    {
        if (cache[term.Id] is { } known)
        {
            return known;
        }

        List<int>? found = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (term.Matches(tokens[i]))
            {
                (found ??= []).Add(i);
            }
        }

        return cache[term.Id] = found is null ? [] : [.. found];
    }

    private static (CrisisTerm[] Terms, string[] CompactKeys) ParsePattern(string id, string? pattern, Dictionary<string, CrisisTerm> terms)
    {
        var words = (pattern ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            throw new InvalidOperationException($"Crisis phrase {id} has no pattern.");
        }

        var parsed = new List<CrisisTerm>();
        var stemsPerTerm = new List<List<string>>();
        foreach (var word in words)
        {
            var exact = new List<string>();
            var prefixes = new List<string>();
            var stems = new List<string>();
            foreach (var alt in word.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var isPrefix = alt.EndsWith('*');
                var stem = CrisisText.NormalizeWord(isPrefix ? alt[..^1] : alt);
                if (stem.Length == 0 || stem.Contains(' '))
                {
                    throw new InvalidOperationException($"Crisis phrase {id} has an unusable word in its pattern.");
                }

                (isPrefix ? prefixes : exact).Add(stem);
                stems.Add(stem);
            }

            if (stems.Count == 0)
            {
                throw new InvalidOperationException($"Crisis phrase {id} has an empty alternative in its pattern.");
            }

            // The same word list always maps to the same term, so positions are computed once per message.
            var key = string.Join('|', exact.Concat(prefixes.Select(p => p + "*")));
            if (!terms.TryGetValue(key, out var term))
            {
                term = new CrisisTerm(terms.Count, [.. exact], [.. prefixes]);
                terms[key] = term;
            }

            parsed.Add(term);
            stemsPerTerm.Add(stems);
        }

        var keys = new List<string> { string.Empty };
        foreach (var stems in stemsPerTerm)
        {
            keys = [.. keys.SelectMany(k => stems.Select(s => k + s)).Take(MaxCompactKeysPerPhrase)];
        }

        return ([.. parsed], [.. keys.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The pattern as a reader sees it: terms joined with " + ", alternatives with " / ", a prefix shown as "…".</summary>
    internal static string Readable(string pattern) =>
        string.Join(" + ", pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => string.Join(" / ", w.Split('|').Select(a => a.EndsWith('*') ? a[..^1] + "…" : a))));

    private sealed class LexiconFile
    {
        public string? Version { get; set; }

        public List<CategoryEntry>? Categories { get; set; }

        public List<PhraseEntry>? Phrases { get; set; }
    }

    private sealed class CategoryEntry
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Title { get; set; }
    }

    private sealed class PhraseEntry
    {
        public string? Id { get; set; }

        public string? Category { get; set; }

        public string? Pattern { get; set; }

        public int Gap { get; set; }

        public string? Example { get; set; }

        public string? Reason { get; set; }

        public string? Risk { get; set; }
    }
}
