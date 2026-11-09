using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Chatbot.Application.Chat;

namespace Chatbot.Infrastructure.Chat.Llm;

/// <summary>
/// The system prompt that ships INSIDE the assembly (no file to lose in a deployment, no path to read at runtime). The first comment line
/// <c>&lt;!-- version: v1 --&gt;</c> is the label for people; the version stored with each reply adds a short hash of the text, so an edit
/// that forgot to raise the label still gets a new version. HTML comments (the TODO marks too) are not sent to the model.
/// </summary>
public sealed partial class SystemPromptText : ISystemPromptProvider
{
    public const string ResourceName = "Chatbot.Infrastructure.Chat.Llm.system-prompt.tr.txt";

    public SystemPromptText(string raw)
    {
        var label = VersionLabel().Match(raw) is { Success: true } m ? m.Groups[1].Value : "v0";
        Text = HtmlComment().Replace(raw.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty).Trim();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)))[..8];
        Version = $"{label}+{hash}";
    }

    public string Text { get; }

    public string Version { get; }

    /// <summary>The embedded prompt. Throws when the resource is missing (a build mistake that must not go unnoticed).</summary>
    public static SystemPromptText LoadEmbedded()
    {
        using var stream = typeof(SystemPromptText).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded system prompt is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new SystemPromptText(reader.ReadToEnd());
    }

    /// <summary>A prompt read from a file (the evaluation command only, to try a variant). Same rules as the embedded one.</summary>
    public static SystemPromptText FromFile(string path) => new(File.ReadAllText(path, Encoding.UTF8));

    [GeneratedRegex(@"<!--\s*version:\s*([A-Za-z0-9._-]{1,16})\s*-->")]
    private static partial Regex VersionLabel();

    [GeneratedRegex(@"<!--.*?-->[ \t]*\n?", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();
}
