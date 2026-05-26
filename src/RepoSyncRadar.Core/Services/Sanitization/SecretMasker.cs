using System.Text.RegularExpressions;

namespace RepoSyncRadar.Core.Services.Sanitization;

/// <summary>
/// Replaces common secret-ish substrings (tokens, API keys, email addresses,
/// IPv4 addresses, phone-shaped digit runs) with stable redacted placeholders
/// before untrusted text is forwarded to Copilot.
/// </summary>
/// <remarks>
/// <para>
/// Each placeholder is of the form <c>***LABEL***</c>. The patterns are
/// applied in a fixed order so that:
/// </para>
/// <list type="bullet">
///   <item>more-specific patterns (e.g. <c>sk-ant-</c>) win over their
///   more-permissive cousins (<c>sk-</c>);</item>
///   <item>the resulting placeholders contain no characters that any later
///   pattern can match, which guarantees an already-masked token is left
///   alone on subsequent passes ("既マスク部分を再マスクしない").</item>
/// </list>
/// </remarks>
public static partial class SecretMasker
{
    // GitHub fine-grained / classic PATs and other gh_* tokens.
    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{36,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubPatRegex();

    // JWT: header.payload.signature, each segment base64url (alphanumerics + - _).
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    // Anthropic keys start with sk-ant- and use dashes/underscores. Match first
    // so they aren't swallowed by the broader OpenAI pattern below.
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKeyRegex();

    // OpenAI / generic sk- keys.
    [GeneratedRegex(@"sk-[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiKeyRegex();

    // Pragmatic email regex; matches the bulk of RFC 5321 addresses we care
    // about in commit messages without trying to be exhaustive.
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // IPv4 dotted-quad.
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    // Phone-shaped 12-digit run (deliberately broader than any single locale).
    [GeneratedRegex(@"\b\d{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    private static readonly (Func<Regex> Pattern, string Replacement)[] _rules =
    [
        (GitHubPatRegex, "***GITHUB_PAT***"),
        (JwtRegex, "***JWT***"),
        (AnthropicKeyRegex, "***ANTHROPIC_KEY***"),
        (OpenAiKeyRegex, "***OPENAI_KEY***"),
        (EmailRegex, "***EMAIL***"),
        (Ipv4Regex, "***IPV4***"),
        (PhoneRegex, "***PHONE***"),
    ];

    /// <summary>
    /// Returns <paramref name="input"/> with every recognised secret-ish
    /// substring replaced by a stable <c>***LABEL***</c> placeholder. Input
    /// that contains pre-existing placeholders is left untouched on those
    /// regions because the placeholder syntax cannot match any of the rules.
    /// </summary>
    public static string Mask(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var current = input;
        foreach (var (pattern, replacement) in _rules)
        {
            current = pattern().Replace(current, replacement);
        }

        return current;
    }
}
