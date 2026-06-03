using System.Globalization;
using System.Net;
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
///   <item><c>{% octicon "name" ... %}</c> → Primer Octicons 相当の SVG に展開する。</item>
///   <item><c>{% raw %}…{% endraw %}</c> → 中身を保護し、評価対象から外す。</item>
///   <item>出力を持たない <c>comment</c> / <c>assign</c> / <c>capture</c> は本文から除去する。</item>
/// </list>
/// 解決できないタグはそのまま残し、後段の
/// <see cref="MarkdownPreviewRenderer"/> でハイライト span に包む。
/// </summary>
internal static partial class DocsLiquidEvaluator
{
    private const int _defaultMaxRecursionDepth = 6;
    private const int _infiniteLoopGuard = 64;
    private const char _rawSentinelStart = '\uE000';
    private const char _rawSentinelEnd = '\uE001';

    // {% raw %}...{% endraw %} — 中身を退避する。
    [GeneratedRegex(@"\{%\s*raw\s*%\}(?<content>.*?)\{%\s*endraw\s*%\}", RegexOptions.Singleline)]
    private static partial Regex RawBlockRegex();

    [GeneratedRegex(@"\{%-?\s*comment\s*-?%\}(?<content>.*?)\{%-?\s*endcomment\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex CommentBlockRegex();

    [GeneratedRegex(@"\{%-?\s*capture\s+[A-Za-z_][A-Za-z0-9_]*\s*-?%\}(?<content>.*?)\{%-?\s*endcapture\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex CaptureBlockRegex();

    [GeneratedRegex(@"\{%-?\s*assign\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<expr>.*?)\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex AssignTagRegex();

    // {% data variables.X.Y %} / {% data reusables.X.Y %} / {% data reusables.X.Y+arg %}
    [GeneratedRegex(@"\{%-?\s*data\s+(?<expr>[A-Za-z0-9_.\-/+_\[\]]+)\s*-?%\}")]
    private static partial Regex DataTagRegex();

    // {% indented_data_reference reusables.X spaces=N %}
    [GeneratedRegex(@"\{%-?\s*indented_data_reference\s+(?<expr>[A-Za-z0-9_.\-/+_]+)(?:\s+spaces=(?<spaces>\d+))?\s*-?%\}")]
    private static partial Regex IndentedDataRegex();

    // {% for entry in tables.copilot.models-and-pricing %}...{% endfor %}
    [GeneratedRegex(@"\{%-?\s*for\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s+in\s+(?<expr>[^%]+?)\s*-?%\}(?<body>(?:(?!\{%-?\s*for\b).)*?)\{%-?\s*endfor\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex ForBlockRegex();

    // {{ entry.model }} within supported for loops.
    [GeneratedRegex(@"\{\{-?\s*(?<var>[A-Za-z_][A-Za-z0-9_]*)\.(?<key>[A-Za-z0-9_-]+)\s*-?\}\}")]
    private static partial Regex LoopVariableExprRegex();

    [GeneratedRegex(@"\{%-?\s*case\s+(?<expr>.*?)\s*-?%\}(?<body>.*?)\{%-?\s*endcase\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex CaseBlockRegex();

    [GeneratedRegex(@"\{%-?\s*(?<kw>when|else)\b\s*(?<expr>[^%]*?)\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex CaseBranchRegex();

    [GeneratedRegex("""^(?<var>[A-Za-z_][A-Za-z0-9_]*)\.(?<key>[A-Za-z0-9_-]+)\s*(?<op>==|!=)\s*(?<quote>["'])(?<value>.*?)\k<quote>$""")]
    private static partial Regex LoopComparisonRegex();

    [GeneratedRegex(@"^(?<var>[A-Za-z_][A-Za-z0-9_]*)\.(?<key>[A-Za-z0-9_-]+)$")]
    private static partial Regex LoopTruthyRegex();

    // {{ variables.X.Y }} / {{ site.data.variables.X.Y }}
    [GeneratedRegex(@"\{\{-?\s*(?<expr>[^{}]*?)\s*-?\}\}", RegexOptions.Singleline)]
    private static partial Regex VariableExprRegex();

    [GeneratedRegex("""^(?<left>.+?)\s*(?<op><=|>=|==|!=|<|>)\s*(?<right>.+)$""")]
    private static partial Regex LiquidComparisonRegex();

    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex ExtraEmptyLinesRegex();

    private static readonly Dictionary<string, OcticonDefinition> _octicons =
        new Dictionary<string, OcticonDefinition>(StringComparer.Ordinal)
        {
            ["check"] = new(
                16,
                16,
                """<path d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z"></path>"""),
            ["dash"] = new(
                16,
                16,
                """<path d="M2 7.75A.75.75 0 0 1 2.75 7h10.5a.75.75 0 0 1 0 1.5H2.75A.75.75 0 0 1 2 7.75Z"></path>"""),
            ["x"] = new(
                16,
                16,
                """<path d="M3.72 3.72a.75.75 0 0 1 1.06 0L8 6.94l3.22-3.22a.749.749 0 0 1 1.275.326.749.749 0 0 1-.215.734L9.06 8l3.22 3.22a.749.749 0 0 1-.326 1.275.749.749 0 0 1-.734-.215L8 9.06l-3.22 3.22a.751.751 0 0 1-1.042-.018.751.751 0 0 1-.018-1.042L6.94 8 3.72 4.78a.75.75 0 0 1 0-1.06Z"></path>"""),
            ["alert"] = new(
                16,
                16,
                """<path d="M6.457 1.047c.659-1.234 2.427-1.234 3.086 0l6.082 11.378A1.75 1.75 0 0 1 14.082 15H1.918a1.75 1.75 0 0 1-1.543-2.575Zm1.763.707a.25.25 0 0 0-.44 0L1.698 13.132a.25.25 0 0 0 .22.368h12.164a.25.25 0 0 0 .22-.368Zm.53 3.996v2.5a.75.75 0 0 1-1.5 0v-2.5a.75.75 0 0 1 1.5 0ZM9 11a1 1 0 1 1-2 0 1 1 0 0 1 2 0Z"></path>"""),
            ["copilot"] = new(
                16,
                16,
                """<path d="M7.998 15.035c-4.562 0-7.873-2.914-7.998-3.749V9.338c.085-.628.677-1.686 1.588-2.065.013-.07.024-.143.036-.218.029-.183.06-.384.126-.612-.201-.508-.254-1.084-.254-1.656 0-.87.128-1.769.693-2.484.579-.733 1.494-1.124 2.724-1.261 1.206-.134 2.262.034 2.944.765.05.053.096.108.139.165.044-.057.094-.112.143-.165.682-.731 1.738-.899 2.944-.765 1.23.137 2.145.528 2.724 1.261.566.715.693 1.614.693 2.484 0 .572-.053 1.148-.254 1.656.066.228.098.429.126.612.012.076.024.148.037.218.924.385 1.522 1.471 1.591 2.095v1.872c0 .766-3.351 3.795-8.002 3.795Zm0-1.485c2.28 0 4.584-1.11 5.002-1.433V7.862l-.023-.116c-.49.21-1.075.291-1.727.291-1.146 0-2.059-.327-2.71-.991A3.222 3.222 0 0 1 8 6.303a3.24 3.24 0 0 1-.544.743c-.65.664-1.563.991-2.71.991-.652 0-1.236-.081-1.727-.291l-.023.116v4.255c.419.323 2.722 1.433 5.002 1.433ZM6.762 2.83c-.193-.206-.637-.413-1.682-.297-1.019.113-1.479.404-1.713.7-.247.312-.369.789-.369 1.554 0 .793.129 1.171.308 1.371.162.181.519.379 1.442.379.853 0 1.339-.235 1.638-.54.315-.322.527-.827.617-1.553.117-.935-.037-1.395-.241-1.614Zm4.155-.297c-1.044-.116-1.488.091-1.681.297-.204.219-.359.679-.242 1.614.091.726.303 1.231.618 1.553.299.305.784.54 1.638.54.922 0 1.28-.198 1.442-.379.179-.2.308-.578.308-1.371 0-.765-.123-1.242-.37-1.554-.233-.296-.693-.587-1.713-.7Z"></path><path d="M6.25 9.037a.75.75 0 0 1 .75.75v1.501a.75.75 0 0 1-1.5 0V9.787a.75.75 0 0 1 .75-.75Zm4.25.75v1.501a.75.75 0 0 1-1.5 0V9.787a.75.75 0 0 1 1.5 0Z"></path>"""),
            ["codescan"] = new(
                16,
                16,
                """<path d="M8.47 4.97a.75.75 0 0 0 0 1.06L9.94 7.5 8.47 8.97a.75.75 0 1 0 1.06 1.06l2-2a.75.75 0 0 0 0-1.06l-2-2a.75.75 0 0 0-1.06 0ZM6.53 6.03a.75.75 0 0 0-1.06-1.06l-2 2a.75.75 0 0 0 0 1.06l2 2a.75.75 0 1 0 1.06-1.06L5.06 7.5l1.47-1.47Z"></path><path d="M12.246 13.307a7.501 7.501 0 1 1 1.06-1.06l2.474 2.473a.749.749 0 0 1-.326 1.275.749.749 0 0 1-.734-.215ZM1.5 7.5a6.002 6.002 0 0 0 3.608 5.504 6.002 6.002 0 0 0 6.486-1.117.748.748 0 0 1 .292-.293A6 6 0 1 0 1.5 7.5Z"></path>"""),
            ["download"] = new(
                16,
                16,
                """<path d="M2.75 14A1.75 1.75 0 0 1 1 12.25v-2.5a.75.75 0 0 1 1.5 0v2.5c0 .138.112.25.25.25h10.5a.25.25 0 0 0 .25-.25v-2.5a.75.75 0 0 1 1.5 0v2.5A1.75 1.75 0 0 1 13.25 14Z"></path><path d="M7.25 7.689V2a.75.75 0 0 1 1.5 0v5.689l1.97-1.969a.749.749 0 1 1 1.06 1.06l-3.25 3.25a.749.749 0 0 1-1.06 0L4.22 6.78a.749.749 0 1 1 1.06-1.06l1.97 1.969Z"></path>"""),
            ["gear"] = new(
                16,
                16,
                """<path d="M8 0a8.2 8.2 0 0 1 .701.031C9.444.095 9.99.645 10.16 1.29l.288 1.107c.018.066.079.158.212.224.231.114.454.243.668.386.123.082.233.09.299.071l1.103-.303c.644-.176 1.392.021 1.82.63.27.385.506.792.704 1.218.315.675.111 1.422-.364 1.891l-.814.806c-.049.048-.098.147-.088.294.016.257.016.515 0 .772-.01.147.038.246.088.294l.814.806c.475.469.679 1.216.364 1.891a7.977 7.977 0 0 1-.704 1.217c-.428.61-1.176.807-1.82.63l-1.102-.302c-.067-.019-.177-.011-.3.071a5.909 5.909 0 0 1-.668.386c-.133.066-.194.158-.211.224l-.29 1.106c-.168.646-.715 1.196-1.458 1.26a8.006 8.006 0 0 1-1.402 0c-.743-.064-1.289-.614-1.458-1.26l-.289-1.106c-.018-.066-.079-.158-.212-.224a5.738 5.738 0 0 1-.668-.386c-.123-.082-.233-.09-.299-.071l-1.103.303c-.644.176-1.392-.021-1.82-.63a8.12 8.12 0 0 1-.704-1.218c-.315-.675-.111-1.422.363-1.891l.815-.806c.05-.048.098-.147.088-.294a6.214 6.214 0 0 1 0-.772c.01-.147-.038-.246-.088-.294l-.815-.806C.635 6.045.431 5.298.746 4.623a7.92 7.92 0 0 1 .704-1.217c.428-.61 1.176-.807 1.82-.63l1.102.302c.067.019.177.011.3-.071.214-.143.437-.272.668-.386.133-.066.194-.158.211-.224l.29-1.106C6.009.645 6.556.095 7.299.03 7.53.01 7.764 0 8 0Zm-.571 1.525c-.036.003-.108.036-.137.146l-.289 1.105c-.147.561-.549.967-.998 1.189-.173.086-.34.183-.5.29-.417.278-.97.423-1.529.27l-1.103-.303c-.109-.03-.175.016-.195.045-.22.312-.412.644-.573.99-.014.031-.021.11.059.19l.815.806c.411.406.562.957.53 1.456a4.709 4.709 0 0 0 0 .582c.032.499-.119 1.05-.53 1.456l-.815.806c-.081.08-.073.159-.059.19.162.346.353.677.573.989.02.03.085.076.195.046l1.102-.303c.56-.153 1.113-.008 1.53.27.161.107.328.204.501.29.447.222.85.629.997 1.189l.289 1.105c.029.109.101.143.137.146a6.6 6.6 0 0 0 1.142 0c.036-.003.108-.036.137-.146l.289-1.105c.147-.561.549-.967.998-1.189.173-.086.34-.183.5-.29.417-.278.97-.423 1.529-.27l1.103.303c.109.029.175-.016.195-.045.22-.313.411-.644.573-.99.014-.031.021-.11-.059-.19l-.815-.806c-.411-.406-.562-.957-.53-1.456a4.709 4.709 0 0 0 0-.582c-.032-.499.119-1.05.53-1.456l.815-.806c.081-.08.073-.159.059-.19a6.464 6.464 0 0 0-.573-.989c-.02-.03-.085-.076-.195-.046l-1.102.303c-.56.153-1.113.008-1.53-.27a4.44 4.44 0 0 0-.501-.29c-.447-.222-.85-.629-.997-1.189l-.289-1.105c-.029-.11-.101-.143-.137-.146a6.6 6.6 0 0 0-1.142 0ZM11 8a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM9.5 8a1.5 1.5 0 1 0-3.001.001A1.5 1.5 0 0 0 9.5 8Z"></path>"""),
            ["kebab-horizontal"] = new(
                16,
                16,
                """<path d="M8 9a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3ZM1.5 9a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Zm13 0a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z"></path>"""),
            ["organization"] = new(
                16,
                16,
                """<path d="M1.75 16A1.75 1.75 0 0 1 0 14.25V1.75C0 .784.784 0 1.75 0h8.5C11.216 0 12 .784 12 1.75v12.5c0 .085-.006.168-.018.25h2.268a.25.25 0 0 0 .25-.25V8.285a.25.25 0 0 0-.111-.208l-1.055-.703a.749.749 0 1 1 .832-1.248l1.055.703c.487.325.779.871.779 1.456v5.965A1.75 1.75 0 0 1 14.25 16h-3.5a.766.766 0 0 1-.197-.026c-.099.017-.2.026-.303.026h-3a.75.75 0 0 1-.75-.75V14h-1v1.25a.75.75 0 0 1-.75.75Zm-.25-1.75c0 .138.112.25.25.25H4v-1.25a.75.75 0 0 1 .75-.75h2.5a.75.75 0 0 1 .75.75v1.25h2.25a.25.25 0 0 0 .25-.25V1.75a.25.25 0 0 0-.25-.25h-8.5a.25.25 0 0 0-.25.25ZM3.75 6h.5a.75.75 0 0 1 0 1.5h-.5a.75.75 0 0 1 0-1.5ZM3 3.75A.75.75 0 0 1 3.75 3h.5a.75.75 0 0 1 0 1.5h-.5A.75.75 0 0 1 3 3.75Zm4 3A.75.75 0 0 1 7.75 6h.5a.75.75 0 0 1 0 1.5h-.5A.75.75 0 0 1 7 6.75ZM7.75 3h.5a.75.75 0 0 1 0 1.5h-.5a.75.75 0 0 1 0-1.5ZM3 9.75A.75.75 0 0 1 3.75 9h.5a.75.75 0 0 1 0 1.5h-.5A.75.75 0 0 1 3 9.75ZM7.75 9h.5a.75.75 0 0 1 0 1.5h-.5a.75.75 0 0 1 0-1.5Z"></path>"""),
            ["triangle-down"] = new(
                16,
                16,
                """<path d="m4.427 7.427 3.396 3.396a.25.25 0 0 0 .354 0l3.396-3.396A.25.25 0 0 0 11.396 7H4.604a.25.25 0 0 0-.177.427Z"></path>"""),
        };

    // {% octicon "gear" aria-hidden="true" aria-label="gear" %}
    // github/docs data YAML can HTML-entity encode quotes inside table cells.
    [GeneratedRegex("""\{%-?\s*octicon\s+(?:["']|&quot;|&#34;|&#x22;)(?<icon>[A-Za-z0-9-]+)(?:["']|&quot;|&#34;|&#x22;)(?<options>.*?)\s*-?%\}""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OcticonTagRegex();

    [GeneratedRegex("""(?<key>[A-Za-z_:][-A-Za-z0-9_:.]*)=(?<quote>["']|&quot;|&#34;|&#x22;)(?<value>.*?)\k<quote>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OcticonOptionRegex();

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
        int maxRecursionDepth = _defaultMaxRecursionDepth)
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

        current = StripNonOutputLiquid(current);
        var scope = new LiquidScope();

        // 2. for / ifversion / if を内側から再帰的に解く。for は data YAML 配列を
        //    簡易展開し、ifversion は version で真評価、if (= 版に依存しない) は
        //    最初の分岐を採用 (保守的)。
        //    variables / reusables の展開で新しい ifversion が現れることがあるため、
        //    反復展開の中でも毎回 ResolveConditionals を通す。
        current = ResolveAssignTagsOutsideForBlocks(current, context, scope);
        current = ResolveForLoops(current, context, version, scope);
        current = ResolveCaseBlocks(current, context, scope);
        current = ResolveConditionals(current, context, version, context.Features, scope);
        current = OcticonTagRegex().Replace(current, ResolveOcticonTag);

        // 3. variables / reusables を反復展開。
        for (var depth = 0; depth < maxRecursionDepth; depth++)
        {
            var before = current;
            current = ResolveAssignTagsOutsideForBlocks(current, context, scope);
            current = ResolveForLoops(current, context, version, scope);
            var dataSource = current;
            current = DataTagRegex().Replace(current, m => ResolveDataExpr(m, context, dataSource));
            current = IndentedDataRegex().Replace(current, m => ResolveIndentedDataExpr(m, context));
            current = ResolveCaseBlocks(current, context, scope);
            current = VariableExprRegex().Replace(current, m => ResolveLiquidVariable(m, context, scope));
            current = ResolveForLoops(current, context, version, scope);
            current = ResolveCaseBlocks(current, context, scope);
            current = ResolveConditionals(current, context, version, context.Features, scope);
            current = OcticonTagRegex().Replace(current, ResolveOcticonTag);
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

        current = CleanUpLiquidPost(current);

        return current;
    }

    /// <summary>
    /// <see cref="DocsVersionCatalog.Default"/> で評価するオーバーロード
    /// (バージョン未指定の経路や旧 API 互換用)。
    /// </summary>
    public static string Evaluate(
        string? source,
        DocsLiquidContext context,
        int maxRecursionDepth = _defaultMaxRecursionDepth)
        => Evaluate(source, context, DocsVersionCatalog.Default, maxRecursionDepth);

    private static string StripNonOutputLiquid(string source)
    {
        var current = CommentBlockRegex().Replace(source, string.Empty);
        return CaptureBlockRegex().Replace(current, string.Empty);
    }

    private static string ResolveDataExpr(Match match, DocsLiquidContext context, string source)
    {
        var expr = match.Groups["expr"].Value;
        var resolved = ResolveDataExpr(expr, context, match.Value);
        return string.Equals(resolved, match.Value, StringComparison.Ordinal)
            ? resolved
            : ApplyDataTagContext(match, source, resolved);
    }

    private static string ResolveDataExpr(string expr, DocsLiquidContext context, string originalTag)
    {
        expr = NormalizeDataExpr(expr);
        if (expr.StartsWith("variables.", StringComparison.Ordinal))
        {
            var key = expr["variables.".Length..];
            if (TryGetValueWithArgumentFallback(context.Variables, key, out var v))
            {
                return v.Trim();
            }
        }
        else if (expr.StartsWith("reusables.", StringComparison.Ordinal))
        {
            var key = expr["reusables.".Length..];
            if (TryGetValueWithArgumentFallback(context.Reusables, key, out var v))
            {
                return v.Trim();
            }
        }
        // 解決不能 — 後段の NeutralizeLiquid に任せるためタグをそのまま残す。
        return originalTag;
    }

    private static string NormalizeDataExpr(string expr)
    {
        if (expr.StartsWith("site.data.", StringComparison.Ordinal))
        {
            return expr["site.data.".Length..];
        }
        if (expr.StartsWith("data.", StringComparison.Ordinal))
        {
            return expr["data.".Length..];
        }
        return expr;
    }

    private static string ResolveAssignTags(string source, DocsLiquidContext context, LiquidScope scope)
    {
        var current = source;
        for (var safety = 0; safety < _infiniteLoopGuard; safety++)
        {
            var m = AssignTagRegex().Match(current);
            if (!m.Success)
            {
                return current;
            }
            var name = m.Groups["name"].Value;
            var expr = NormalizeLiquidTagExpression(m.Groups["expr"].Value);
            scope.Set(name, EvaluateLiquidExpression(expr, context, scope));
            current = current[..m.Index] + current[(m.Index + m.Length)..];
        }
        return current;
    }

    private static string NormalizeLiquidTagExpression(string expression)
    {
        var trimmed = expression.Trim();
        return trimmed.Length > 0 && trimmed[^1] == '-' ? trimmed[..^1].TrimEnd() : trimmed;
    }

    private static string ResolveAssignTagsOutsideForBlocks(string source, DocsLiquidContext context, LiquidScope scope)
    {
        var sb = new StringBuilder(source.Length);
        var cursor = 0;
        var depth = 0;
        foreach (Match tag in Regex.Matches(source, @"\{%-?\s*(?<tag>for|endfor)\b.*?-?%\}", RegexOptions.Singleline))
        {
            var segment = source[cursor..tag.Index];
            sb.Append(depth == 0 ? ResolveAssignTags(segment, context, scope) : segment);
            sb.Append(tag.Value);
            depth += string.Equals(tag.Groups["tag"].Value, "for", StringComparison.Ordinal) ? 1 : -1;
            if (depth < 0)
            {
                depth = 0;
            }
            cursor = tag.Index + tag.Length;
        }
        var tail = source[cursor..];
        sb.Append(depth == 0 ? ResolveAssignTags(tail, context, scope) : tail);
        return sb.ToString();
    }

    private static string ResolveForLoops(string source, DocsLiquidContext context, DocsVersion version, LiquidScope scope)
    {
        var current = source;
        for (var safety = 0; safety < _infiniteLoopGuard; safety++)
        {
            var replaced = ForBlockRegex().Replace(current, m => ResolveForBlock(m, context, version, scope));
            if (string.Equals(replaced, current, StringComparison.Ordinal))
            {
                break;
            }
            current = replaced;
        }
        return current;
    }

    private static string ResolveForBlock(Match match, DocsLiquidContext context, DocsVersion version, LiquidScope scope)
    {
        var variableName = match.Groups["var"].Value;
        var sequenceExpr = match.Groups["expr"].Value;
        var sequenceValue = EvaluateLiquidExpression(sequenceExpr, context, scope);
        var items = EnumerateForItems(sequenceValue).ToArray();
        if (items.Length == 0)
        {
            var sequenceKey = NormalizeDataExpr(sequenceExpr);
            if (!context.DataSequences.TryGetValue(sequenceKey, out var rows))
            {
                return IsKnownOptionalPreviewLoop(sequenceExpr) ? string.Empty : match.Value;
            }
            items = rows.Select(RowToLiquidObject).ToArray();
        }

        var body = match.Groups["body"].Value;
        var sb = new StringBuilder(body.Length * Math.Min(items.Length, 4));
        foreach (var item in items)
        {
            var childScope = new LiquidScope(scope);
            childScope.Set(variableName, item);
            var rendered = ResolveAssignTagsOutsideForBlocks(body, context, childScope);
            rendered = ResolveForLoops(rendered, context, version, childScope);
            rendered = ResolveAssignTags(rendered, context, childScope);
            EnsureLanguageSupportLevel(childScope);
            rendered = ResolveCaseBlocks(rendered, context, childScope);
            rendered = ResolveConditionals(rendered, context, version, context.Features, childScope);
            rendered = ResolveLiquidVariables(rendered, context, childScope);
            rendered = ReplaceLanguageSupportDynamicVariable(rendered, childScope);
            sb.Append(rendered);
        }
        return sb.ToString();
    }

    private static bool IsKnownOptionalPreviewLoop(string expression)
        => expression.Contains("ideEntry.", StringComparison.Ordinal)
            || expression.Contains("groupVersions", StringComparison.Ordinal);

    private static void EnsureLanguageSupportLevel(LiquidScope scope)
    {
        if ((scope.TryGet("supportLevel", out var existing) && IsTruthy(existing))
            || !scope.TryGet("languageData", out var languageData)
            || !scope.TryGet("featureKey", out var featureKey)
            || languageData is not DocsLiquidObjectValue languageDataObject
            || !languageDataObject.Properties.TryGetValue(RenderLiquidValue(featureKey).Trim(), out var supportLevel))
        {
            return;
        }
        scope.Set("supportLevel", supportLevel);
    }

    private static string ReplaceLanguageSupportDynamicVariable(string source, LiquidScope scope)
    {
        if (!source.Contains("languageData[featureKey]", StringComparison.Ordinal)
            || !scope.TryGet("languageData", out var languageData)
            || !scope.TryGet("featureKey", out var featureKey)
            || languageData is not DocsLiquidObjectValue languageDataObject
            || !languageDataObject.Properties.TryGetValue(RenderLiquidValue(featureKey).Trim(), out var supportLevel))
        {
            return source;
        }
        return source.Replace("{{ languageData[featureKey] }}", RenderLiquidValue(supportLevel), StringComparison.Ordinal);
    }

    private static DocsLiquidDataValue RowToLiquidObject(IReadOnlyDictionary<string, string> row)
        => new DocsLiquidObjectValue(row.ToDictionary(
            static pair => pair.Key,
            static pair => (DocsLiquidDataValue)new DocsLiquidScalarValue(pair.Value),
            StringComparer.Ordinal));

    private static IEnumerable<DocsLiquidDataValue> EnumerateForItems(DocsLiquidDataValue value)
        => value switch
        {
            DocsLiquidSequenceValue sequence => sequence.Items,
            DocsLiquidObjectValue obj => obj.Properties.Select(static pair =>
                (DocsLiquidDataValue)new DocsLiquidSequenceValue([
                    new DocsLiquidScalarValue(pair.Key),
                    pair.Value,
                ])),
            _ => [],
        };

    private static string ResolveLiquidVariables(string source, DocsLiquidContext context, LiquidScope scope)
        => VariableExprRegex().Replace(source, m => ResolveLiquidVariable(m, context, scope));

    private static string ResolveLiquidVariable(Match match, DocsLiquidContext context, LiquidScope scope)
    {
        var expr = NormalizeLiquidTagExpression(match.Groups["expr"].Value);
        if (expr.Contains("languageData", StringComparison.Ordinal)
            && expr.Contains("featureKey", StringComparison.Ordinal)
            && scope.TryGet("languageData", out var languageData)
            && scope.TryGet("featureKey", out var featureKey)
            && languageData is DocsLiquidObjectValue languageDataObject
            && languageDataObject.Properties.TryGetValue(RenderLiquidValue(featureKey).Trim(), out var supportLevel))
        {
            return RenderLiquidValue(supportLevel);
        }
        if (TryResolveScopedDynamicBracket(expr, scope, out var dynamicValue))
        {
            return RenderLiquidValue(dynamicValue);
        }
        var value = EvaluateLiquidExpression(expr, context, scope);
        var rendered = RenderLiquidValue(value);
        if (rendered.Length == 0 && (scope.Contains(expr)
            || expr.Contains("languageData", StringComparison.Ordinal)
            || expr.StartsWith("enterpriseServerReleases.", StringComparison.Ordinal)))
        {
            return string.Empty;
        }
        return rendered.Length == 0 ? ResolveDataExpr(expr, context, match.Value) : rendered;
    }

    private static bool TryEvaluateLiquidCondition(string condition, DocsLiquidContext context, LiquidScope? scope, out bool result)
    {
        result = false;
        if (scope is null)
        {
            return false;
        }

        var trimmed = condition.Trim();
        var comparison = LiquidComparisonRegex().Match(trimmed);
        if (comparison.Success)
        {
            var left = RenderLiquidValue(EvaluateLiquidExpression(comparison.Groups["left"].Value, context, scope));
            var right = RenderLiquidValue(EvaluateLiquidExpression(comparison.Groups["right"].Value, context, scope));
            var order = CompareLiquidScalars(left, right);
            result = comparison.Groups["op"].Value switch
            {
                "<" => order < 0,
                "<=" => order <= 0,
                ">" => order > 0,
                ">=" => order >= 0,
                "==" => string.Equals(left, right, StringComparison.Ordinal),
                "!=" => !string.Equals(left, right, StringComparison.Ordinal),
                _ => false,
            };
            return true;
        }

        var value = EvaluateLiquidExpression(trimmed, context, scope);
        result = IsTruthy(value);
        return !ReferenceEquals(value, DocsLiquidDataValue.EmptyString) || scope.Contains(trimmed) || context.DataObjects.ContainsKey(trimmed);
    }

    private static int CompareLiquidScalars(string left, string right)
    {
        if (long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftNumber)
            && long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static bool IsTruthy(DocsLiquidDataValue value)
        => value switch
        {
            DocsLiquidScalarValue scalar => !string.IsNullOrWhiteSpace(scalar.Value)
                && !string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase),
            DocsLiquidSequenceValue sequence => sequence.Items.Count > 0,
            DocsLiquidObjectValue obj => obj.Properties.Count > 0,
            _ => false,
        };

    private static string ApplyDataTagContext(Match match, string source, string text)
    {
        if (!text.Contains('\n', StringComparison.Ordinal))
        {
            return text;
        }

        var lineStart = match.Index;
        while (lineStart > 0 && source[lineStart - 1] != '\n')
        {
            lineStart--;
        }
        var prefix = source[lineStart..match.Index];
        if (string.IsNullOrEmpty(prefix))
        {
            return text;
        }

        var blockquotePrefix = MatchBlockquotePrefix(prefix);
        if (blockquotePrefix is not null)
        {
            return PrefixContinuationLines(text, blockquotePrefix);
        }
        if (prefix.All(static c => c is ' ' or '\t'))
        {
            return PrefixContinuationLines(text, prefix);
        }
        return text;
    }

    private static string? MatchBlockquotePrefix(string prefix)
    {
        var index = 0;
        while (index < prefix.Length && prefix[index] is ' ' or '\t')
        {
            index++;
        }
        if (index >= prefix.Length || prefix[index] != '>')
        {
            return null;
        }
        index++;
        if (index < prefix.Length && prefix[index] is ' ' or '\t')
        {
            index++;
        }
        return prefix[..index];
    }

    private static string PrefixContinuationLines(string text, string prefix)
    {
        var sb = new StringBuilder(text.Length + prefix.Length * 4);
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            sb.Append(ch);
            if (ch == '\n' && index + 1 < text.Length)
            {
                sb.Append(prefix);
            }
        }
        return sb.ToString();
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

    private static string ResolveOcticonTag(Match match)
    {
        var iconName = NormalizeOcticonName(match.Groups["icon"].Value);
        var options = ParseOcticonOptions(match.Groups["options"].Value);
        if (!options.ContainsKey("aria-label"))
        {
            options["aria-label"] = string.Create(
                CultureInfo.InvariantCulture,
                $"{DefaultOcticonLabel(iconName)} icon");
        }

        if (!_octicons.TryGetValue(iconName, out var definition))
        {
            return RenderFallbackOcticonSvg(iconName, options);
        }

        return RenderOcticonSvg(iconName, definition, options);
    }

    private static string NormalizeOcticonName(string iconName)
        => iconName switch
        {
            "clippy" => "paste",
            "duplicate" => "copy",
            "trashcan" => "trash",
            _ => iconName,
        };

    private static Dictionary<string, string> ParseOcticonOptions(string optionsSource)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match option in OcticonOptionRegex().Matches(optionsSource))
        {
            var key = option.Groups["key"].Value;
            var value = option.Groups["value"].Value;
            options[key] = value;
            if (string.Equals(key, "label", StringComparison.Ordinal))
            {
                options["aria-label"] = value;
            }
        }
        return options;
    }

    private static string RenderOcticonSvg(
        string iconName,
        OcticonDefinition definition,
        Dictionary<string, string> options)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "1.1",
            ["width"] = definition.Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = definition.Height.ToString(CultureInfo.InvariantCulture),
            ["viewBox"] = string.Create(CultureInfo.InvariantCulture, $"0 0 {definition.Width} {definition.Height}"),
            ["class"] = string.Create(CultureInfo.InvariantCulture, $"octicon octicon-{iconName}"),
            ["aria-hidden"] = "true",
            ["data-component"] = "Octicon",
        };

        foreach (var (key, value) in options)
        {
            attributes[key] = value;
        }
        var requestedWidth = options.TryGetValue("width", out var width) ? width : null;
        var requestedHeight = options.TryGetValue("height", out var height) ? height : null;
        if (requestedWidth is not null || requestedHeight is not null)
        {
            ApplyOcticonSize(attributes, definition, requestedWidth, requestedHeight);
        }
        if (options.TryGetValue("class", out var extraClass))
        {
            attributes["class"] = string.Create(
                CultureInfo.InvariantCulture,
                $"octicon octicon-{iconName} {extraClass}").TrimEnd();
        }
        if (options.ContainsKey("aria-label"))
        {
            attributes["role"] = "img";
            attributes.Remove("aria-hidden");
        }

        var sb = new StringBuilder(definition.Path.Length + attributes.Count * 32 + 16);
        sb.Append("<svg");
        foreach (var (key, value) in attributes)
        {
            sb.Append(' ')
                .Append(key)
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(value))
                .Append('"');
        }
        sb.Append('>')
            .Append(definition.Path)
            .Append("</svg>");
        return sb.ToString();
    }

    private static string RenderFallbackOcticonSvg(string iconName, Dictionary<string, string> options)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "1.1",
            ["width"] = "16",
            ["height"] = "16",
            ["viewBox"] = "0 0 16 16",
            ["class"] = string.Create(CultureInfo.InvariantCulture, $"octicon octicon-{iconName} rsr-octicon-fallback"),
            ["data-component"] = "Octicon",
            ["role"] = "img",
        };

        foreach (var (key, value) in options)
        {
            if (string.Equals(key, "class", StringComparison.Ordinal))
            {
                attributes["class"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"octicon octicon-{iconName} rsr-octicon-fallback {value}").TrimEnd();
                continue;
            }
            attributes[key] = value;
        }
        attributes.Remove("aria-hidden");

        var sb = new StringBuilder(256);
        sb.Append("<svg");
        foreach (var (key, value) in attributes)
        {
            sb.Append(' ')
                .Append(key)
                .Append("=\"")
                .Append(WebUtility.HtmlEncode(value))
                .Append('"');
        }
        sb.Append("><circle cx=\"8\" cy=\"8\" r=\"5.5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\"></circle></svg>");
        return sb.ToString();
    }

    private static void ApplyOcticonSize(
        Dictionary<string, string> attributes,
        OcticonDefinition definition,
        string? requestedWidth,
        string? requestedHeight)
    {
        if (!string.IsNullOrEmpty(requestedWidth))
        {
            attributes["width"] = requestedWidth;
        }
        if (!string.IsNullOrEmpty(requestedHeight))
        {
            attributes["height"] = requestedHeight;
        }
        if (!string.IsNullOrEmpty(requestedWidth) && string.IsNullOrEmpty(requestedHeight)
            && int.TryParse(requestedWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth))
        {
            attributes["height"] = ((parsedWidth * definition.Height) / definition.Width).ToString(CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrEmpty(requestedWidth) && !string.IsNullOrEmpty(requestedHeight)
            && int.TryParse(requestedHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight))
        {
            attributes["width"] = ((parsedHeight * definition.Width) / definition.Height).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string DefaultOcticonLabel(string iconName)
        => NonAlphanumericRegex().Replace(iconName.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphanumericRegex();

    private static string ResolveCaseBlocks(string source, DocsLiquidContext context, LiquidScope scope)
        => CaseBlockRegex().Replace(source, m => ResolveCaseBlock(m, context, scope));

    private static string ResolveCaseBlock(Match match, DocsLiquidContext context, LiquidScope scope)
    {
        var value = RenderLiquidValue(EvaluateLiquidExpression(match.Groups["expr"].Value, context, scope));
        var body = match.Groups["body"].Value;
        var branches = CaseBranchRegex().Matches(body);
        for (var i = 0; i < branches.Count; i++)
        {
            var branch = branches[i];
            var branchStart = branch.Index + branch.Length;
            var branchEnd = i + 1 < branches.Count ? branches[i + 1].Index : body.Length;
            var branchBody = body[branchStart..branchEnd];
            if (string.Equals(branch.Groups["kw"].Value, "else", StringComparison.Ordinal))
            {
                return branchBody;
            }

            var expected = RenderLiquidValue(EvaluateLiquidExpression(branch.Groups["expr"].Value, context, scope));
            if (string.Equals(value, expected, StringComparison.Ordinal))
            {
                return branchBody;
            }
        }
        return string.Empty;
    }

    private static DocsLiquidDataValue EvaluateLiquidExpression(string expression, DocsLiquidContext context, LiquidScope scope)
    {
        var parts = SplitLiquidFilters(expression);
        var value = EvaluateLiquidPrimary(parts[0], context, scope);
        foreach (var filter in parts.Skip(1))
        {
            value = ApplyLiquidFilter(value, filter, context, scope);
        }
        return value;
    }

    private static List<string> SplitLiquidFilters(string expression)
    {
        var parts = new List<string>();
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == '|')
            {
                parts.Add(expression[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(expression[start..].Trim());
        return parts;
    }

    private static DocsLiquidDataValue ApplyLiquidFilter(
        DocsLiquidDataValue value,
        string filter,
        DocsLiquidContext context,
        LiquidScope scope)
    {
        var trimmed = filter.Trim();
        if (string.Equals(trimmed, "first", StringComparison.Ordinal))
        {
            return value is DocsLiquidSequenceValue { Items.Count: > 0 } sequence
                ? sequence.Items[0]
                : DocsLiquidDataValue.EmptyString;
        }
        if (trimmed.StartsWith("default:", StringComparison.Ordinal))
        {
            return IsTruthy(value)
                ? value
                : EvaluateLiquidExpression(trimmed["default:".Length..], context, scope);
        }
        if (trimmed.StartsWith("date:", StringComparison.Ordinal))
        {
            var format = UnquoteLiquidString(trimmed["date:".Length..].Trim());
            var rendered = RenderLiquidValue(value);
            if (string.Equals(format, "%s", StringComparison.Ordinal))
            {
                if (string.Equals(rendered, "now", StringComparison.OrdinalIgnoreCase))
                {
                    return new DocsLiquidScalarValue(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                }
                if (DateTimeOffset.TryParse(rendered, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return new DocsLiquidScalarValue(parsed.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        return value;
    }

    private static DocsLiquidDataValue EvaluateLiquidPrimary(string expression, DocsLiquidContext context, LiquidScope scope)
    {
        var trimmed = expression.Trim();
        if (trimmed.Length == 0)
        {
            return DocsLiquidDataValue.EmptyString;
        }
        if (IsQuotedLiquidString(trimmed))
        {
            return new DocsLiquidScalarValue(UnquoteLiquidString(trimmed));
        }
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return new DocsLiquidScalarValue(trimmed);
        }
        if (scope.TryGet(trimmed, out var scopedValue))
        {
            return scopedValue;
        }
        if (TryResolveScopedDynamicBracket(trimmed, scope, out var dynamicValue))
        {
            return dynamicValue;
        }
        if (context.DataObjects.TryGetValue(trimmed, out var dataObject))
        {
            return dataObject;
        }
        if (TryResolveLiquidPath(trimmed, context, scope, out var pathValue))
        {
            return pathValue;
        }
        var dataResolved = ResolveDataExpr(trimmed, context, string.Empty);
        return dataResolved.Length > 0 ? new DocsLiquidScalarValue(dataResolved) : DocsLiquidDataValue.EmptyString;
    }

    private static bool TryResolveScopedDynamicBracket(string expression, LiquidScope scope, out DocsLiquidDataValue value)
    {
        value = DocsLiquidDataValue.EmptyString;
        var trimmed = expression.Trim();
        var open = trimmed.IndexOf('[', StringComparison.Ordinal);
        var close = trimmed.IndexOf(']', open + 1);
        if (open <= 0 || close <= open + 1 || close != trimmed.Length - 1)
        {
            return false;
        }

        var rootName = trimmed[..open].Trim();
        var keyName = trimmed[(open + 1)..close].Trim();
        if (!scope.TryGet(rootName, out var root) || !scope.TryGet(keyName, out var keyValue))
        {
            return false;
        }
        return TryGetObjectProperty(root, RenderLiquidValue(keyValue).Trim(), out value);
    }

    private static bool TryResolveLiquidPath(string expression, DocsLiquidContext context, LiquidScope scope, out DocsLiquidDataValue value)
    {
        value = DocsLiquidDataValue.EmptyString;
        if (!TryGetLiquidPathRoot(expression, context, scope, out var rootName, out value))
        {
            return false;
        }

        var index = rootName.Length;
        while (index < expression.Length)
        {
            if (expression[index] == '.')
            {
                index++;
                var start = index;
                while (index < expression.Length && expression[index] is not '.' and not '[')
                {
                    index++;
                }
                if (!TryGetObjectProperty(value, expression[start..index], out value))
                {
                    return false;
                }
                continue;
            }
            if (expression[index] == '[')
            {
                var end = expression.IndexOf(']', index + 1);
                if (end < 0)
                {
                    return false;
                }
                var accessor = expression[(index + 1)..end].Trim();
                if (!TryApplyBracketAccessor(value, accessor, context, scope, out value))
                {
                    return false;
                }
                index = end + 1;
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryGetLiquidPathRoot(
        string expression,
        DocsLiquidContext context,
        LiquidScope scope,
        out string rootName,
        out DocsLiquidDataValue value)
    {
        foreach (var key in context.DataObjects.Keys.OrderByDescending(static key => key.Length))
        {
            if (expression.Length == key.Length || (expression.StartsWith(key, StringComparison.Ordinal) && expression[key.Length] is '.' or '['))
            {
                rootName = key;
                value = context.DataObjects[key];
                return true;
            }
        }

        var end = expression.IndexOfAny(['.', '[']);
        rootName = end < 0 ? expression : expression[..end];
        return scope.TryGet(rootName, out value!);
    }

    private static bool TryApplyBracketAccessor(
        DocsLiquidDataValue source,
        string accessor,
        DocsLiquidContext context,
        LiquidScope scope,
        out DocsLiquidDataValue value)
    {
        value = DocsLiquidDataValue.EmptyString;
        string key;
        if (IsQuotedLiquidString(accessor))
        {
            key = UnquoteLiquidString(accessor);
        }
        else if (scope.TryGet(accessor, out var scopedAccessor))
        {
            key = RenderLiquidValue(scopedAccessor);
        }
        else
        {
            key = RenderLiquidValue(EvaluateLiquidExpression(accessor, context, scope));
        }
        if (source is DocsLiquidSequenceValue sequence && int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            if (index >= 0 && index < sequence.Items.Count)
            {
                value = sequence.Items[index];
                return true;
            }
            return false;
        }
        return TryGetObjectProperty(source, key, out value);
    }

    private static bool TryGetObjectProperty(DocsLiquidDataValue source, string key, out DocsLiquidDataValue value)
    {
        value = DocsLiquidDataValue.EmptyString;
        if (source is DocsLiquidObjectValue obj && obj.Properties.TryGetValue(key, out value!))
        {
            return true;
        }
        return false;
    }

    private static string RenderLiquidValue(DocsLiquidDataValue value)
        => value switch
        {
            DocsLiquidScalarValue scalar => scalar.Value,
            DocsLiquidSequenceValue sequence => string.Join(", ", sequence.Items.Select(RenderLiquidValue)),
            _ => string.Empty,
        };

    private static bool IsQuotedLiquidString(string value)
        => value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'));

    private static string UnquoteLiquidString(string value)
        => IsQuotedLiquidString(value.Trim()) ? value.Trim()[1..^1] : value.Trim();

    private sealed class LiquidScope
    {
        private readonly Dictionary<string, DocsLiquidDataValue> _values = new(StringComparer.Ordinal);
        private readonly LiquidScope? _parent;

        public LiquidScope(LiquidScope? parent = null)
        {
            _parent = parent;
        }

        public void Set(string name, DocsLiquidDataValue value)
            => _values[name] = value;

        public bool Contains(string name)
            => _values.ContainsKey(name) || (_parent?.Contains(name) ?? false);

        public bool TryGet(string name, out DocsLiquidDataValue value)
        {
            if (_values.TryGetValue(name, out value!))
            {
                return true;
            }
            if (_parent is not null)
            {
                return _parent.TryGet(name, out value!);
            }
            value = DocsLiquidDataValue.EmptyString;
            return false;
        }
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

    private static string ResolveConditionals(
        string source,
        DocsLiquidContext context,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features,
        LiquidScope? scope = null)
    {
        var current = source;
        for (var safety = 0; safety < _infiniteLoopGuard; safety++)
        {
            var replaced = InnermostIfBlockRegex().Replace(current, m =>
            {
                var tag = m.Groups["tag"].Value;
                var cond = m.Groups["cond"].Value;
                var body = m.Groups["body"].Value;
                return EvaluateBlock(tag, cond, body, context, version, features, scope);
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
    private static string EvaluateBlock(
        string tag,
        string cond,
        string body,
        DocsLiquidContext context,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features,
        LiquidScope? scope)
    {
        var isVersion = string.Equals(tag, "ifversion", StringComparison.Ordinal);

        // body を elsif / else の境界で「最初の if 分岐 + 後続分岐群」に分割。
        var separators = BranchSeparatorRegex().Matches(body);
        if (separators.Count == 0)
        {
            return EvaluateCondition(cond, isVersion, context, version, features, scope) ? body : string.Empty;
        }

        // 0 番目 = if 本体 (条件: cond)
        var firstBranchBody = body[..separators[0].Index];
        if (EvaluateCondition(cond, isVersion, context, version, features, scope))
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
            if (EvaluateCondition(branchCond, isVersion, context, version, features, scope))
            {
                return branchBody;
            }
        }

        return string.Empty;
    }

    private static bool EvaluateCondition(
        string condition,
        bool isVersion,
        DocsLiquidContext context,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features,
        LiquidScope? scope)
    {
        if (isVersion)
        {
            return VersionExpressionEvaluator.Evaluate(condition, version, features);
        }
        if (TryEvaluateLiquidCondition(condition, context, scope, out var loopConditionResult))
        {
            return loopConditionResult;
        }

        // {% if X %} は版とは無関係の Liquid 条件式 (truthiness)。
        // フル Liquid 評価器は実装していないため、保守的に true 扱いとし最初の分岐を採用する。
        return true;
    }

    private static string CreateRawSentinel(int index)
        => string.Create(CultureInfo.InvariantCulture, $"{_rawSentinelStart}RAW{index}{_rawSentinelEnd}");

    private static string CleanUpLiquidPost(string source)
        => ExtraEmptyLinesRegex().Replace(source, "\n\n");

    private sealed record OcticonDefinition(int Width, int Height, string Path);
}
