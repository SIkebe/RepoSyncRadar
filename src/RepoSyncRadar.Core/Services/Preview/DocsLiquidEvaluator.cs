using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// github/docs の Liquid テンプレート (<c>{% ... %}</c> / <c>{{ ... }}</c>) を
/// プレビュー目的で最小限評価する pure 関数群 (IMPLEMENTATION_PLAN.md §Step 19.8/19.9)。
/// 完全な Liquid エンジンは実装せず、レビュー時に視覚ノイズになりやすい代表的な
/// タグだけを <see cref="DocsLiquidContext"/> をルックアップして展開する。
/// <list type="bullet">
///   <item><c>{% data variables.X.Y %}</c> / <c>{{ variables.X.Y }}</c> →
///   <see cref="DocsLiquidContext.Variables"/> から値を取得。</item>
///   <item><c>{% data reusables.X.Y %}</c> / <c>{% data reusables.X.Y+arg %}</c> →
///   <see cref="DocsLiquidContext.Reusables"/> 本文を埋め込み (再帰評価)。</item>
///   <item><c>{% indented_data_reference reusables.X spaces=N %}</c> →
///   reusables を取得し、各行に <c>spaces</c> 個の空白を prefix。</item>
///   <item><c>{% ifversion X %}A{% elsif Y %}B{% else %}C{% endif %}</c> →
///   <see cref="VersionExpressionEvaluator"/> で <see cref="DocsVersion"/>
///   に対して各分岐の条件式を順に評価し、最初に真となった分岐の body を返す
///   (公式 docs と同じ挙動 — §Step 19.9 で first-branch-only から置換)。
///   どの分岐も真でなければ <c>{% else %}</c> → なければ空。</item>
///   <item><c>{% if X %}A{% endif %}</c> → 版に依存しないため、最初の分岐を採用
///   (公式 docs 上は条件式が真の場合のみ表示されるが、レビューでは保守的に表示)。</item>
///   <item><c>{% raw %}…{% endraw %}</c> → 中身を保護し、評価対象から外す。</item>
/// </list>
/// 解決できないタグはそのまま残し、後段の
/// <see cref="MarkdownPreviewRenderer"/> でハイライト span に包む。
/// </summary>
internal static partial class DocsLiquidEvaluator
{
    private const int DefaultMaxRecursionDepth = 6;
    private const int InfiniteLoopGuard = 64;
    private const char RawSentinelStart = '\uE000';
    private const char RawSentinelEnd = '\uE001';

    // {% raw %}...{% endraw %} — 中身を退避する。
    [GeneratedRegex(@"\{%\s*raw\s*%\}(?<content>.*?)\{%\s*endraw\s*%\}", RegexOptions.Singleline)]
    private static partial Regex RawBlockRegex();

    // {% data variables.X.Y %} / {% data reusables.X.Y %} / {% data reusables.X.Y+arg %}
    [GeneratedRegex(@"\{%-?\s*data\s+(?<expr>[A-Za-z0-9_.\-/+]+)\s*-?%\}")]
    private static partial Regex DataTagRegex();

    // {% indented_data_reference reusables.X spaces=N %}
    [GeneratedRegex(@"\{%-?\s*indented_data_reference\s+(?<expr>[A-Za-z0-9_.\-/+]+)(?:\s+spaces=(?<spaces>\d+))?\s*-?%\}")]
    private static partial Regex IndentedDataRegex();

    // {{ variables.X.Y }} / {{ X.Y }}
    [GeneratedRegex(@"\{\{-?\s*(?<expr>[A-Za-z0-9_.\-/]+)\s*-?\}\}")]
    private static partial Regex VariableExprRegex();

    // 最も内側の {% if(version) %}...{% endif %}。body に if/ifversion を含まない
    // ものだけマッチするので、反復置換でネストを下から解決できる。
    // <see cref="EvaluateBlock"/> が tag (if/ifversion) と先頭条件式 cond を分けて使う。
    [GeneratedRegex(
        @"\{%-?\s*(?<tag>if(?:version)?)\b\s*(?<cond>[^%]*?)\s*-?%\}(?<body>(?:(?!\{%-?\s*if(?:version)?\b).)*?)\{%-?\s*endif\s*-?%\}",
        RegexOptions.Singleline)]
    private static partial Regex InnermostIfBlockRegex();

    // body の中で elsif / else の境界を見つける (条件式も同時に取得)。
    [GeneratedRegex(@"\{%-?\s*(?<kw>elsif|else)\b\s*(?<cond>[^%]*?)\s*-?%\}")]
    private static partial Regex BranchSeparatorRegex();

    // raw 退避用 sentinel: \uE000RAW{n}\uE001
    [GeneratedRegex(@"\uE000RAW(?<i>\d+)\uE001")]
    private static partial Regex RawSentinelRegex();

    /// <summary>
    /// Liquid タグを <paramref name="context"/> で展開して返す。<paramref name="version"/>
    /// に対して <c>{% ifversion ... %}</c> を真評価し、選ばれた分岐の body だけを残す。
    /// 展開後に再び Liquid タグが現れることがあるため (reusables が variables を含む等)、
    /// 結果が安定するか深さが <paramref name="maxRecursionDepth"/> に達するまで反復する。
    /// </summary>
    public static string Evaluate(
        string? source,
        DocsLiquidContext context,
        DocsVersion version,
        int maxRecursionDepth = DefaultMaxRecursionDepth)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source ?? string.Empty;
        }
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(version);

        // 1. raw blocks を sentinel に退避 (Liquid 構文をリテラル表示したいブロックを守る)。
        var rawSegments = new List<string>();
        var current = RawBlockRegex().Replace(source, m =>
        {
            rawSegments.Add(m.Groups["content"].Value);
            return CreateRawSentinel(rawSegments.Count - 1);
        });

        // 2. ifversion / if を内側から再帰的に解く。ifversion は version で真評価、
        //    if (= 版に依存しない) は最初の分岐を採用 (保守的)。
        //    variables / reusables の展開で新しい ifversion が現れることがあるため、
        //    反復展開の中でも毎回 ResolveConditionals を通す。
        current = ResolveConditionals(current, version);

        // 3. variables / reusables を反復展開。
        for (var depth = 0; depth < maxRecursionDepth; depth++)
        {
            var before = current;
            current = DataTagRegex().Replace(current, m => ResolveDataExpr(m.Groups["expr"].Value, context, m.Value));
            current = IndentedDataRegex().Replace(current, m => ResolveIndentedDataExpr(m, context));
            current = VariableExprRegex().Replace(current, m => ResolveDataExpr(m.Groups["expr"].Value, context, m.Value));
            current = ResolveConditionals(current, version);
            if (string.Equals(before, current, StringComparison.Ordinal))
            {
                break;
            }
        }

        // 4. raw を復元 (リテラルとして残す)。
        if (rawSegments.Count > 0)
        {
            current = RawSentinelRegex().Replace(current, m =>
            {
                var idx = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
                return idx >= 0 && idx < rawSegments.Count ? rawSegments[idx] : m.Value;
            });
        }

        return current;
    }

    /// <summary>
    /// <see cref="DocsVersionCatalog.Default"/> で評価するオーバーロード
    /// (バージョン未指定の経路や旧 API 互換用)。
    /// </summary>
    public static string Evaluate(
        string? source,
        DocsLiquidContext context,
        int maxRecursionDepth = DefaultMaxRecursionDepth)
        => Evaluate(source, context, DocsVersionCatalog.Default, maxRecursionDepth);

    private static string ResolveDataExpr(string expr, DocsLiquidContext context, string originalTag)
    {
        if (expr.StartsWith("variables.", StringComparison.Ordinal))
        {
            var key = expr["variables.".Length..];
            if (TryGetValueWithArgumentFallback(context.Variables, key, out var v))
            {
                return v;
            }
        }
        else if (expr.StartsWith("reusables.", StringComparison.Ordinal))
        {
            var key = expr["reusables.".Length..];
            if (TryGetValueWithArgumentFallback(context.Reusables, key, out var v))
            {
                return v;
            }
        }
        // 解決不能 — 後段の NeutralizeLiquid に任せるためタグをそのまま残す。
        return originalTag;
    }

    private static string ResolveIndentedDataExpr(Match m, DocsLiquidContext context)
    {
        var expr = m.Groups["expr"].Value;
        if (!expr.StartsWith("reusables.", StringComparison.Ordinal))
        {
            return m.Value;
        }
        var key = expr["reusables.".Length..];
        if (!TryGetValueWithArgumentFallback(context.Reusables, key, out var v))
        {
            return m.Value;
        }

        var spacesGroup = m.Groups["spaces"];
        var spaces = 0;
        if (spacesGroup.Success
            && int.TryParse(spacesGroup.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            spaces = parsed;
        }
        if (spaces == 0)
        {
            return v;
        }
        return IndentLines(v, spaces);
    }

    private static bool TryGetValueWithArgumentFallback(
        IReadOnlyDictionary<string, string> source,
        string key,
        out string value)
    {
        if (source.TryGetValue(key, out value!))
        {
            return true;
        }

        var plus = key.IndexOf('+', StringComparison.Ordinal);
        if (plus <= 0)
        {
            return false;
        }

        return source.TryGetValue(key[..plus], out value!);
    }

    private static string IndentLines(string content, int spaces)
    {
        var indent = new string(' ', spaces);
        var sb = new StringBuilder(content.Length + spaces * 8);
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                AppendIndentedSegment(sb, content.AsSpan(start, i - start), indent);
                sb.Append('\n');
                start = i + 1;
            }
        }
        if (start < content.Length)
        {
            AppendIndentedSegment(sb, content.AsSpan(start), indent);
        }
        return sb.ToString();
    }

    private static void AppendIndentedSegment(StringBuilder sb, ReadOnlySpan<char> segment, string indent)
    {
        // \r を尊重しつつ、空行はインデントしない (markdown のブロック分割を壊さない)。
        var trimmed = segment.TrimEnd('\r');
        if (trimmed.Length == 0)
        {
            // 空行 (改行のみ) — \r を維持して append しない。
            sb.Append(segment);
            return;
        }
        sb.Append(indent);
        sb.Append(segment);
    }

    private static string ResolveConditionals(string source, DocsVersion version)
    {
        var current = source;
        for (var safety = 0; safety < InfiniteLoopGuard; safety++)
        {
            var replaced = InnermostIfBlockRegex().Replace(current, m =>
            {
                var tag = m.Groups["tag"].Value;
                var cond = m.Groups["cond"].Value;
                var body = m.Groups["body"].Value;
                return EvaluateBlock(tag, cond, body, version);
            });
            if (string.Equals(replaced, current, StringComparison.Ordinal))
            {
                break;
            }
            current = replaced;
        }
        return current;
    }

    /// <summary>
    /// 単一の <c>{% if/ifversion cond %}body{% endif %}</c> ブロックを評価し、
    /// 採用すべき分岐の本文 (= elsif/else 境界で切り出した片) を返す。
    /// どの分岐も真でなく <c>{% else %}</c> も無ければ空文字列。
    /// </summary>
    private static string EvaluateBlock(string tag, string cond, string body, DocsVersion version)
    {
        var isVersion = string.Equals(tag, "ifversion", StringComparison.Ordinal);

        // body を elsif / else の境界で「最初の if 分岐 + 後続分岐群」に分割。
        var separators = BranchSeparatorRegex().Matches(body);
        if (separators.Count == 0)
        {
            return EvaluateCondition(cond, isVersion, version) ? body : string.Empty;
        }

        // 0 番目 = if 本体 (条件: cond)
        var firstBranchBody = body[..separators[0].Index];
        if (EvaluateCondition(cond, isVersion, version))
        {
            return firstBranchBody;
        }

        // 後続 elsif/else を順に試す。
        for (var i = 0; i < separators.Count; i++)
        {
            var sep = separators[i];
            var kw = sep.Groups["kw"].Value;
            var branchCond = sep.Groups["cond"].Value;
            var branchStart = sep.Index + sep.Length;
            var branchEnd = i + 1 < separators.Count ? separators[i + 1].Index : body.Length;
            var branchBody = body[branchStart..branchEnd];

            if (string.Equals(kw, "else", StringComparison.Ordinal))
            {
                return branchBody;
            }
            if (EvaluateCondition(branchCond, isVersion, version))
            {
                return branchBody;
            }
        }

        return string.Empty;
    }

    private static bool EvaluateCondition(string condition, bool isVersion, DocsVersion version)
    {
        if (isVersion)
        {
            return VersionExpressionEvaluator.Evaluate(condition, version);
        }
        // {% if X %} は版とは無関係の Liquid 条件式 (truthiness)。
        // フル Liquid 評価器は実装していないため、保守的に true 扱いとし最初の分岐を採用する。
        return true;
    }

    private static string CreateRawSentinel(int index)
        => string.Create(CultureInfo.InvariantCulture, $"{RawSentinelStart}RAW{index}{RawSentinelEnd}");
}
