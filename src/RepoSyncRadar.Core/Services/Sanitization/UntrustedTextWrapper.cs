namespace RepoSyncRadar.Core.Services.Sanitization;

/// <summary>
/// Wraps untrusted text (commit messages, diffs, fetched HTML) in explicit
/// fence markers before it is concatenated into a Copilot prompt.
/// </summary>
/// <remarks>
/// <para>
/// The fences (<c>&lt;&lt;&lt;UNTRUSTED:{title}&gt;&gt;&gt;</c> ...
/// <c>&lt;&lt;&lt;END&gt;&gt;&gt;</c>) make it visually obvious to the model
/// that the enclosed region is <em>data</em>, not instructions. Any occurrence
/// of those fences inside the user-supplied content is neutralised so a
/// hostile commit body cannot forge a closing fence and inject new
/// instructions afterwards. See <c>docs/DESIGN.md</c> §8.3.
/// </para>
/// </remarks>
public static class UntrustedTextWrapper
{
    private const string _openPrefix = "<<<UNTRUSTED:";
    private const string _openSuffix = ">>>";
    private const string _closeMarker = "<<<END>>>";

    private const string _openPrefixEscaped = "<<<UNTRUSTED-ESCAPED:";
    private const string _closeMarkerEscaped = "<<<END-ESCAPED>>>";

    /// <summary>
    /// Returns <paramref name="content"/> wrapped in untrusted-data fences.
    /// Embedded fence markers in either the title or the content are replaced
    /// with escaped variants so they cannot terminate the wrapper.
    /// </summary>
    public static string Wrap(string title, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(content);

        var safeTitle = Neutralise(title);
        var safeContent = Neutralise(content);

        return $"{_openPrefix}{safeTitle}{_openSuffix}\n{safeContent}\n{_closeMarker}";
    }

    private static string Neutralise(string value)
    {
        return value
            .Replace(_openPrefix, _openPrefixEscaped, StringComparison.Ordinal)
            .Replace(_closeMarker, _closeMarkerEscaped, StringComparison.Ordinal);
    }
}
