namespace Chatbot.Application.Options;

/// <summary>The effective settings of one profile and what is wrong with the choice. Messages name SETTINGS only, never values.</summary>
public sealed record LlmResolution(LlmOptions Effective, IReadOnlyList<string> Problems);

/// <summary>
/// Turns the bound <see cref="LlmOptions"/> (flat settings + named profiles) into the one set of effective settings a model call uses.
/// The flat <c>Llm:*</c> settings are the implicit default profile and are returned as they are, so a deployment that never heard of
/// profiles behaves exactly as before. A profile takes its endpoint, key, model and extra body only from itself.
/// </summary>
public static class LlmProfiles
{
    public const string DefaultLabel = "varsayılan";

    /// <summary>1-40 characters: lowercase ASCII letters, digits and hyphens.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 40 } && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <param name="raw">The settings as bound from configuration (or an effective set; resolving again is harmless).</param>
    /// <param name="profile">Empty / null selects the flat settings. <paramref name="source"/> names where the choice came from, for messages.</param>
    public static LlmResolution Resolve(LlmOptions raw, string? profile, string source = "Llm:ActiveProfile")
    {
        var name = profile?.Trim() ?? string.Empty;
        var effective = raw.Copy();
        effective.ActiveProfile = null;
        effective.Profiles = new Dictionary<string, LlmProfileOptions>(StringComparer.Ordinal);

        if (name.Length == 0)
        {
            return new LlmResolution(effective, []);
        }

        if (!IsValidName(name))
        {
            return new LlmResolution(effective, [$"{source} must be a profile name (lowercase letters, digits and hyphens, at most 40 characters)."]);
        }

        if (raw.Profiles is null || !raw.Profiles.TryGetValue(name, out var p) || p is null)
        {
            return new LlmResolution(effective, [$"{source} names a profile that is not defined (no Llm:Profiles:{name}:* settings)."]);
        }

        effective.ProfileName = name;
        effective.ModelOptional = true;
        effective.BaseUrl = p.BaseUrl;
        effective.AllowedHosts = p.AllowedHosts ?? [];
        effective.AuthScheme = string.IsNullOrWhiteSpace(p.AuthScheme) ? LlmOptions.BearerAuth : p.AuthScheme.Trim();
        effective.AuthHeaderName = string.IsNullOrWhiteSpace(p.AuthHeaderName) ? "X-API-Key" : p.AuthHeaderName.Trim();
        effective.ApiKey = p.ApiKey;
        effective.Model = string.IsNullOrWhiteSpace(p.Model) ? null : p.Model.Trim();
        effective.ExtraBodyJson = p.ExtraBodyJson;

        // The limits fall back to the flat settings; they are not secrets and do not point anywhere.
        effective.MaxTokens = p.MaxTokens ?? raw.MaxTokens;
        effective.MaxTokensParameter = string.IsNullOrWhiteSpace(p.MaxTokensParameter) ? raw.MaxTokensParameter : p.MaxTokensParameter.Trim();
        effective.FirstTokenTimeoutSeconds = p.FirstTokenTimeoutSeconds ?? raw.FirstTokenTimeoutSeconds;
        effective.IdleTimeoutSeconds = p.IdleTimeoutSeconds ?? raw.IdleTimeoutSeconds;
        effective.TotalTimeoutSeconds = p.TotalTimeoutSeconds ?? raw.TotalTimeoutSeconds;
        effective.MaxRetries = p.MaxRetries ?? raw.MaxRetries;
        return new LlmResolution(effective, []);
    }
}
