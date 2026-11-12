namespace Chatbot.Application.Options;

/// <summary>
/// Keeps a model call from leaving for a place nobody approved: the base URL must be https (plain http only for localhost in
/// Development), carry no credentials, and name a host that is on the allow list. Messages name the SETTING, never the value.
/// </summary>
public static class LlmEndpointPolicy
{
    /// <param name="prefix">The prefix of the setting names in messages (<c>Llm:</c> or <c>Llm:Profiles:&lt;name&gt;:</c>).</param>
    /// <param name="httpsOnly">True for a profile without authentication: plain http is refused even for localhost in Development.</param>
    public static IReadOnlyList<string> Check(string? baseUrl, IEnumerable<string> allowedHosts, bool development, string prefix = "Llm:", bool httpsOnly = false)
    {
        var problems = new List<string>();
        var hosts = allowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList();

        if (hosts.Count == 0)
        {
            problems.Add($"{prefix}AllowedHosts is empty: name the host the base URL may point to.");
        }

        if (hosts.Any(h => h.Contains('*', StringComparison.Ordinal) || h.Contains('/', StringComparison.Ordinal) || h.Contains(':', StringComparison.Ordinal)))
        {
            problems.Add($"{prefix}AllowedHosts takes host names only (no wildcard, scheme, port or path).");
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            problems.Add($"{prefix}BaseUrl is missing or not an absolute URL.");
            return problems;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problems.Add($"{prefix}BaseUrl must not contain credentials, a query string or a fragment.");
        }

        var local = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(development && !httpsOnly && local && uri.Scheme == Uri.UriSchemeHttp))
        {
            problems.Add(httpsOnly
                ? $"{prefix}BaseUrl must use https (a profile without authentication never uses plain http)."
                : $"{prefix}BaseUrl must use https (plain http is accepted only for localhost in Development).");
        }

        if (!hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{prefix}BaseUrl points to a host that is not in {prefix}AllowedHosts.");
        }

        return problems;
    }

    private static readonly string[] ReservedHeaders = ["Authorization", "Content-Type", "Content-Length", "Host", "Accept", "Cookie", "Transfer-Encoding", "Connection"];

    /// <summary>
    /// A header that may carry the key: 1-64 letters, digits, <c>- _ .</c> (no space, colon, line break or non-ASCII), and none of the
    /// headers the client sets itself. The key must not be able to replace Authorization, the body type or the host.
    /// </summary>
    public static bool IsValidHeaderName(string? name) =>
        name is { Length: > 0 and <= 64 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && !ReservedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Letters, digits and <c>. _ - : / @</c> (providers write names like "org/model:tag"), at most 100 characters.</summary>
    public static bool IsValidModelName(string name) =>
        name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/' or '@');
}
