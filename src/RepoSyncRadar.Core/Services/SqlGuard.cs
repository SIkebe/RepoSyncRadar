using System.Text.RegularExpressions;

namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Validates a user-provided SQL query before it is executed via the <c>radar_query</c>
/// Copilot tool (IMPLEMENTATION_PLAN.md §Step 18). The guard is intentionally strict —
/// it rejects anything that is not a single, read-only <c>SELECT</c> against a known
/// table.
/// </summary>
/// <remarks>
/// <para>
/// Defence-in-depth: even though the SQLite connection used by <c>radar_query</c> opens
/// the database in read-only mode, that does not block PRAGMAs, ATTACH, or SQL injection
/// vectors via accidental concatenation. The guard runs <em>before</em> the query is
/// shown to SQLite at all.
/// </para>
/// </remarks>
public sealed partial class SqlGuard
{
    private const int DefaultLimit = 100;

    /// <summary>Tables the radar agent is allowed to read.</summary>
    public static readonly IReadOnlySet<string> AllowedTables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Commits",
            "Files",
            "Reviews",
            "Drafts",
            "Scores",
            "IgnoreRules",
            "BoostRules",
            "Audits",
            "PathUrlMap",
        };

    private static readonly IReadOnlySet<string> ForbiddenKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "DELETE", "DROP", "ATTACH", "PRAGMA",
            "CREATE", "ALTER", "REPLACE", "VACUUM", "REINDEX",
            "BEGIN", "COMMIT", "ROLLBACK", "TRUNCATE", "EXEC", "EXECUTE",
            "GRANT", "REVOKE",
        };

    [GeneratedRegex(@"--[^\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"/\*[\s\S]*?\*/", RegexOptions.CultureInvariant)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"'([^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"\b(FROM|JOIN)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableRefRegex();

    [GeneratedRegex(@"\blimit\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LimitRegex();

    /// <summary>
    /// Validates and optionally augments <paramref name="sql"/>. Parameters are passed
    /// through unchanged; positional <c>?</c> bindings inside string literals are
    /// preserved because comment/literal stripping only happens internally.
    /// </summary>
    public static SqlGuardResult Validate(string sql, IReadOnlyList<object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        parameters ??= [];
        var trimmed = sql.Trim();
        if (trimmed.Length == 0)
        {
            return SqlGuardResult.Failure("空の SQL は受け付けられません。", parameters);
        }

        var stripped = StripCommentsAndLiterals(trimmed);
        // Count statement separators outside trailing whitespace.
        var statementCount = CountStatements(stripped);
        if (statementCount > 1)
        {
            return SqlGuardResult.Failure("複数の SQL 文は許可されていません。", parameters);
        }

        if (!StartsWithSelect(stripped))
        {
            return SqlGuardResult.Failure("SELECT 文以外は許可されていません。", parameters);
        }

        foreach (var keyword in ForbiddenKeywords)
        {
            if (ContainsWord(stripped, keyword))
            {
                return SqlGuardResult.Failure($"禁止キーワード '{keyword}' を含んでいます。", parameters);
            }
        }

        foreach (Match m in TableRefRegex().Matches(stripped))
        {
            var name = m.Groups[2].Value;
            if (!AllowedTables.Contains(name))
            {
                return SqlGuardResult.Failure(
                    $"テーブル '{name}' は許可リストに含まれていません。", parameters);
            }
        }

        var transformed = trimmed.TrimEnd(';', ' ', '\t', '\r', '\n');
        if (!LimitRegex().IsMatch(stripped))
        {
            transformed = $"{transformed}\nLIMIT {DefaultLimit}";
        }
        return SqlGuardResult.Success(transformed, parameters);
    }

    private static string StripCommentsAndLiterals(string sql)
    {
        var noLineComments = LineCommentRegex().Replace(sql, " ");
        var noBlockComments = BlockCommentRegex().Replace(noLineComments, " ");
        var noStrings = StringLiteralRegex().Replace(noBlockComments, "''");
        return noStrings;
    }

    private static int CountStatements(string strippedSql)
    {
        // Split on ';' and ignore empty trailing pieces.
        var pieces = strippedSql.Split(';');
        var count = 0;
        foreach (var p in pieces)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                count++;
            }
        }
        return count;
    }

    private static bool StartsWithSelect(string strippedSql)
    {
        var s = strippedSql.TrimStart();
        if (s.Length < 6)
        {
            return false;
        }
        return s.AsSpan(0, 6).Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && (s.Length == 6 || char.IsWhiteSpace(s[6]) || s[6] == '(');
    }

    private static bool ContainsWord(string haystack, string word)
    {
        var idx = 0;
        while (idx < haystack.Length)
        {
            var found = haystack.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }
            var before = found == 0 || !IsIdentifierChar(haystack[found - 1]);
            var afterIdx = found + word.Length;
            var after = afterIdx == haystack.Length || !IsIdentifierChar(haystack[afterIdx]);
            if (before && after)
            {
                return true;
            }
            idx = found + 1;
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>Outcome of a <see cref="SqlGuard"/> validation.</summary>
public sealed record SqlGuardResult(
    bool IsValid,
    string? Reason,
    string TransformedSql,
    IReadOnlyList<object?> Parameters)
{
    public static SqlGuardResult Success(string transformed, IReadOnlyList<object?> parameters)
        => new(true, null, transformed, parameters);

    public static SqlGuardResult Failure(string reason, IReadOnlyList<object?> parameters)
        => new(false, reason, string.Empty, parameters);
}
