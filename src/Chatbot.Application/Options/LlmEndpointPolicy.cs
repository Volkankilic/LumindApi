namespace Chatbot.Application.Options;

/// <summary>
/// Keeps a model call from leaving for a place nobody approved: the base URL must be https (plain http only for localhost in
/// Development), carry no credentials, and name a host that is on the allow list. Messages name the SETTING, never the value.
/// </summary>
public static class LlmEndpointPolicy
{
    public static IReadOnlyList<string> Check(string? baseUrl, IEnumerable<string> allowedHosts, bool development)
    {
        var problems = new List<string>();
        var hosts = allowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList();

        if (hosts.Count == 0)
        {
            problems.Add("Llm:AllowedHosts is empty: name the host the base URL may point to.");
        }

        if (hosts.Any(h => h.Contains('*', StringComparison.Ordinal) || h.Contains('/', StringComparison.Ordinal) || h.Contains(':', StringComparison.Ordinal)))
        {
            problems.Add("Llm:AllowedHosts takes host names only (no wildcard, scheme, port or path).");
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            problems.Add("Llm:BaseUrl is missing or not an absolute URL.");
            return problems;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problems.Add("Llm:BaseUrl must not contain credentials, a query string or a fragment.");
        }

        var local = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(development && local && uri.Scheme == Uri.UriSchemeHttp))
        {
            problems.Add("Llm:BaseUrl must use https (plain http is accepted only for localhost in Development).");
        }

        if (!hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("Llm:BaseUrl points to a host that is not in Llm:AllowedHosts.");
        }

        return problems;
    }

    /// <summary>Letters, digits and <c>. _ - : / @</c> (providers write names like "org/model:tag"), at most 100 characters.</summary>
    public static bool IsValidModelName(string name) =>
        name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/' or '@');
}
