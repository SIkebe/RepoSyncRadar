using System.Globalization;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// <c>{% ifversion ... %}</c> の条件式を recursive descent parser で構文解析し、
/// <see cref="DocsVersion"/> に対して真評価する pure 関数群
/// (IMPLEMENTATION_PLAN.md §Step 19.9)。
/// <para>
/// 対応する文法:
/// <code>
/// expr     := orExpr
/// orExpr   := andExpr ('or' andExpr)*
/// andExpr  := unaryExpr ('and' unaryExpr)*
/// unaryExpr:= 'not' unaryExpr | primary
/// primary  := '(' expr ')' | comparison | identifier
/// comparison := 'ghes' compOp version
/// compOp   := '&lt;' | '&lt;=' | '&gt;' | '&gt;=' | '=' | '!='
/// identifier := /[a-zA-Z_][a-zA-Z0-9_-]*/
/// version  := /\d+(\.\d+)*/
/// </code>
/// </para>
/// <para>
/// 識別子の評価ルール:
/// <list type="bullet">
///   <item><c>fpt</c> / <c>ghec</c> / <c>ghes</c>: plan 一致。</item>
///   <item><c>ghae</c>: 廃止 plan として常に <c>false</c>。</item>
///   <item>それ以外 (= feature flag 名): <c>data/features/*.yml</c> が読めていれば
///   その <c>versions</c> mapping、読めていなければ保守的に <c>true</c>。</item>
/// </list>
/// </para>
/// </summary>
internal static class VersionExpressionEvaluator
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyFeatures =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public static bool Evaluate(string? expression, DocsVersion version)
        => Evaluate(expression, version, EmptyFeatures);

    public static bool Evaluate(
        string? expression,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(features);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        try
        {
            var tokens = Tokenize(expression);
            if (tokens.Count == 0)
            {
                return true;
            }
            var pos = 0;
            var result = ParseOr(tokens, ref pos, version, features);
            if (pos != tokens.Count)
            {
                // 余ったトークン — 解析しきれていないので保守的に true。
                return true;
            }
            return result;
        }
        catch (FormatException)
        {
            // 文法的に未対応な式は保守的に true 扱い (本文を残す)。
            return true;
        }
    }

    private enum TokenKind
    {
        Identifier,
        Number,
        LParen,
        RParen,
        OpLt,
        OpLe,
        OpGt,
        OpGe,
        OpEq,
        OpNe,
        KeywordAnd,
        KeywordOr,
        KeywordNot,
    }

    private readonly record struct Token(TokenKind Kind, string Value);

    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < expression.Length)
        {
            var ch = expression[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }
            if (ch == '(')
            {
                tokens.Add(new Token(TokenKind.LParen, "("));
                i++;
                continue;
            }
            if (ch == ')')
            {
                tokens.Add(new Token(TokenKind.RParen, ")"));
                i++;
                continue;
            }
            if (ch == '<')
            {
                if (i + 1 < expression.Length && expression[i + 1] == '=')
                {
                    tokens.Add(new Token(TokenKind.OpLe, "<="));
                    i += 2;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.OpLt, "<"));
                    i++;
                }
                continue;
            }
            if (ch == '>')
            {
                if (i + 1 < expression.Length && expression[i + 1] == '=')
                {
                    tokens.Add(new Token(TokenKind.OpGe, ">="));
                    i += 2;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.OpGt, ">"));
                    i++;
                }
                continue;
            }
            if (ch == '=')
            {
                tokens.Add(new Token(TokenKind.OpEq, "="));
                i++;
                continue;
            }
            if (ch == '!')
            {
                if (i + 1 < expression.Length && expression[i + 1] == '=')
                {
                    tokens.Add(new Token(TokenKind.OpNe, "!="));
                    i += 2;
                    continue;
                }
                throw new FormatException("'!' must be followed by '='");
            }
            if (char.IsDigit(ch))
            {
                var start = i;
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                {
                    i++;
                }
                tokens.Add(new Token(TokenKind.Number, expression[start..i]));
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < expression.Length
                    && (char.IsLetterOrDigit(expression[i]) || expression[i] == '-' || expression[i] == '_'))
                {
                    i++;
                }
                var word = expression[start..i];
                var kind = word switch
                {
                    "and" => TokenKind.KeywordAnd,
                    "or" => TokenKind.KeywordOr,
                    "not" => TokenKind.KeywordNot,
                    _ => TokenKind.Identifier,
                };
                tokens.Add(new Token(kind, word));
                continue;
            }
            throw new FormatException($"Unexpected character '{ch}' in version expression.");
        }
        return tokens;
    }

    private static bool ParseOr(
        List<Token> tokens,
        ref int pos,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        var left = ParseAnd(tokens, ref pos, version, features);
        while (pos < tokens.Count && tokens[pos].Kind == TokenKind.KeywordOr)
        {
            pos++;
            var right = ParseAnd(tokens, ref pos, version, features);
            left = left || right;
        }
        return left;
    }

    private static bool ParseAnd(
        List<Token> tokens,
        ref int pos,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        var left = ParseUnary(tokens, ref pos, version, features);
        while (pos < tokens.Count && tokens[pos].Kind == TokenKind.KeywordAnd)
        {
            pos++;
            var right = ParseUnary(tokens, ref pos, version, features);
            left = left && right;
        }
        return left;
    }

    private static bool ParseUnary(
        List<Token> tokens,
        ref int pos,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.KeywordNot)
        {
            pos++;
            return !ParseUnary(tokens, ref pos, version, features);
        }
        return ParsePrimary(tokens, ref pos, version, features);
    }

    private static bool ParsePrimary(
        List<Token> tokens,
        ref int pos,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        if (pos >= tokens.Count)
        {
            throw new FormatException("Unexpected end of expression.");
        }
        var tok = tokens[pos];
        if (tok.Kind == TokenKind.LParen)
        {
            pos++;
            var inner = ParseOr(tokens, ref pos, version, features);
            if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.RParen)
            {
                throw new FormatException("Missing ')'.");
            }
            pos++;
            return inner;
        }
        if (tok.Kind == TokenKind.Identifier)
        {
            pos++;
            if (pos < tokens.Count && IsComparisonOp(tokens[pos].Kind))
            {
                var op = tokens[pos].Kind;
                pos++;
                if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Number)
                {
                    throw new FormatException("Expected version number after comparison operator.");
                }
                var rightVersion = tokens[pos].Value;
                pos++;
                return EvaluateComparison(tok.Value, op, rightVersion, version);
            }
            return EvaluateIdentifier(tok.Value, version, features);
        }
        throw new FormatException($"Unexpected token '{tok.Value}'.");
    }

    private static bool IsComparisonOp(TokenKind kind)
        => kind is TokenKind.OpLt
            or TokenKind.OpLe
            or TokenKind.OpGt
            or TokenKind.OpGe
            or TokenKind.OpEq
            or TokenKind.OpNe;

    private static bool EvaluateIdentifier(
        string ident,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
        => ident switch
        {
            "fpt" => version.Plan == DocsPlan.Fpt,
            "ghec" => version.Plan == DocsPlan.Ghec,
            "ghes" => version.Plan == DocsPlan.Ghes,
            "ghae" => false,
            _ => EvaluateFeatureIdentifier(ident, version, features),
        };

    private static bool EvaluateFeatureIdentifier(
        string ident,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        if (!features.TryGetValue(ident, out var versions))
        {
            return true;
        }

        var planKey = version.Plan switch
        {
            DocsPlan.Fpt => "fpt",
            DocsPlan.Ghec => "ghec",
            DocsPlan.Ghes => "ghes",
            _ => string.Empty,
        };
        if (planKey.Length == 0 || !versions.TryGetValue(planKey, out var value))
        {
            return false;
        }
        return EvaluateFeatureVersionValue(value, version);
    }

    private static bool EvaluateFeatureVersionValue(string? value, DocsVersion version)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }
        if (string.Equals(trimmed, "*", StringComparison.Ordinal)
            || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (version.Plan != DocsPlan.Ghes || string.IsNullOrEmpty(version.GhesRelease))
        {
            return true;
        }
        return EvaluateGhesFeatureConstraint(trimmed, version.GhesRelease);
    }

    private static bool EvaluateGhesFeatureConstraint(string expression, string release)
    {
        var index = 0;
        var sawConstraint = false;
        while (index < expression.Length)
        {
            while (index < expression.Length && (char.IsWhiteSpace(expression[index]) || expression[index] == ','))
            {
                index++;
            }
            if (index >= expression.Length)
            {
                break;
            }

            var opStart = index;
            if (expression[index] is '<' or '>' or '=' or '!')
            {
                index++;
                if (index < expression.Length && expression[index] == '=')
                {
                    index++;
                }
            }
            else
            {
                return true;
            }
            var op = expression[opStart..index];

            while (index < expression.Length && char.IsWhiteSpace(expression[index]))
            {
                index++;
            }
            var versionStart = index;
            while (index < expression.Length && (char.IsDigit(expression[index]) || expression[index] == '.'))
            {
                index++;
            }
            if (versionStart == index)
            {
                return true;
            }

            sawConstraint = true;
            var cmp = CompareReleaseStrings(release, expression[versionStart..index]);
            var matches = op switch
            {
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                "=" => cmp == 0,
                "!=" => cmp != 0,
                _ => true,
            };
            if (!matches)
            {
                return false;
            }
        }
        return sawConstraint;
    }

    private static bool EvaluateComparison(string ident, TokenKind op, string rightVersion, DocsVersion version)
    {
        // 比較式は ghes の release number に対してのみ意味がある。
        if (!string.Equals(ident, "ghes", StringComparison.Ordinal))
        {
            return false;
        }
        if (version.Plan != DocsPlan.Ghes || string.IsNullOrEmpty(version.GhesRelease))
        {
            return false;
        }
        var cmp = CompareReleaseStrings(version.GhesRelease, rightVersion);
        return op switch
        {
            TokenKind.OpLt => cmp < 0,
            TokenKind.OpLe => cmp <= 0,
            TokenKind.OpGt => cmp > 0,
            TokenKind.OpGe => cmp >= 0,
            TokenKind.OpEq => cmp == 0,
            TokenKind.OpNe => cmp != 0,
            _ => false,
        };
    }

    private static int CompareReleaseStrings(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var len = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < len; i++)
        {
            var l = i < leftParts.Length
                && int.TryParse(leftParts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lp)
                ? lp : 0;
            var r = i < rightParts.Length
                && int.TryParse(rightParts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rp)
                ? rp : 0;
            var c = l.CompareTo(r);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }
}
