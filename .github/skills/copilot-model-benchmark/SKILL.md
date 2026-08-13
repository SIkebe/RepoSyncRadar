---
name: copilot-model-benchmark
description: RepoSyncRadar が GitHub Copilot SDK 経由で使う新旧モデルや reasoning effort を、同一の実リポジトリ課題で品質・正確性・引用・リスク発見・AI Credits・トークン・所要時間・信頼性まで再現可能に比較する Skill。SDK既定モデル更新、GPT/Claude/Gemini 等の比較、新モデル評価、high/max 比較、費用対効果検証で使用する。
---

# Copilot モデル比較ベンチマーク

RepoSyncRadar が GitHub Copilot SDK セッションで使うモデルまたは reasoning effort を、同一課題で比較する。モデル名や印象ではなく、検証可能な回答品質、実測利用量、失敗率を基に `Copilot:DefaultModel` と `Copilot:ReasoningEffort` の推奨を決める。

## 使用場面

- 新しいモデルが追加されたとき
- RepoSyncRadar のSDK既定モデルを変更するとき
- モデル間の品質、AI Credits、トークン消費を比較するとき
- `high`、`xhigh`、`max` など reasoning effort を比較するとき
- 高品質モデルと軽量モデルの使い分けを決めるとき

## 原則

1. **同一条件にする。**
   - 同じリポジトリ、commit、prompt、context tier、reasoning effort、権限、CLI versionで比較する。
   - effort比較ではmodelだけを固定し、effort以外を変えない。
   - 各runは新規sessionにし、resumeしない。

2. **読み取り専用にする。**
   - ベンチマークでファイル変更、shell実行、test実行、外部MCP利用を許可しない。
   - modelへ公開するtoolは`view`、`glob`、`grep`、`lsp`だけに限定し、`skill`、subagent、web、shell、write toolを公開しない。
   - `--deny-tool=write --deny-tool='shell(*)' --disable-builtin-mcps` を指定する。
   - `copilot mcp list --json`でuser、workspace、plugin由来の全MCP server名を取得し、それぞれを`--disable-mcp-server`で無効化する。列挙に失敗した場合はベンチマークを実行しない。
   - raw prompt、model出力、timing、session artifactはリポジトリへ保存しない。機密情報を含まない成功率、中央値、範囲、provenanceのaggregate baselineは、このSkillの比較基準として保存してよい。

3. **実際のコード理解を測る。**
   - 単純な知識問題ではなく、複数ファイルにまたがる呼び出し経路、状態遷移、例外処理、テスト範囲を調べさせる。
   - 正解をsource/testから機械的または人手で検証できる課題にする。
   - 最低1問は「高確度の不足またはリスク。なければ無理に作らない」を含める。
   - この静的コード理解ベンチマークは安全かつ再現しやすい一次評価であり、Morning Triage の実データ採点品質そのものとは区別する。
   - リリース既定値を変更する場合は、可能なら固定fixtureまたは同一commit集合に対するSDKワークフローの比較も追加する。

4. **prompt完全性を検証する。**
   - PowerShellから渡すpromptは改行なしの単一文字列にする。複数行here-stringは使用しない。
   - 回答冒頭に一意なmarkerを要求する。例: `BENCHMARK-7Q`。
   - marker欠落、質問欠落の主張、clarificationだけの回答は品質0点にせず「信頼性失敗」として別記録する。
   - 失敗runを黙って差し替えない。再実行する場合は初回失敗とrerunを両方報告する。

5. **単発結果を過信しない。**
   - 簡易比較は各条件1run、既定モデル決定は各条件3runを推奨する。
   - 3runでは実行順を交互またはランダム化し、成功率、品質中央値、AI Credits中央値を使う。
   - n=1の結果には必ずその制約を明記する。

## 事前記録

比較前に次を記録する。

```powershell
copilot.cmd --version
copilot.cmd help config
git rev-parse HEAD
git status --short
```

- `copilot.cmd help config` から現在利用可能なmodel IDを確認する。
- GitHub DocsのSupported models、AI model comparison、Models and pricingも確認し、GA/preview、公式用途、料金を補助情報として記録する。
- dirty worktreeは変更しない。比較対象コードに未コミット変更がある場合は、その状態を結果へ明記する。

## 標準課題

対象リポジトリから、以下を満たす1機能を選ぶ。

- UI/API entry pointから中核処理まで3層以上を通る
- 永続化または状態遷移がある
- cancellationまたはerror cleanupがある
- 権限、安全境界、入力検証のいずれかがある
- sourceとtestの両方がある

質問は7問を基本とする。

1. entry pointから完了後refreshまでの呼び出し経路
2. 通常処理の正確な順序とearly return
3. timeout、並列数、分岐条件、sharding
4. 状態遷移の閾値、変更可能状態、保護状態、missing-row挙動
5. tool/handler登録数とpermission差、管理ポリシー例外
6. cancellation/error時のabort、dispose、進捗、通知
7. source/testから確認できる高確度の不足またはリスク1件

対象機能に存在しない観点は、同程度に検証可能な観点へ置換する。回答にはrepo相対`file:line`、確認済み事実と推論の区別、上限語数、事実数と未知数の自己申告を要求する。

## 実行テンプレート

artifactはリポジトリ外へ保存する。

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDir = Join-Path $HOME ".copilot\model-benchmarks\$stamp"
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$model = "MODEL_ID"
$effort = "high"
$prompt = 'READ-ONLY BENCHMARK. Do not edit files, run shell commands, or run tests. Start the answer with BENCHMARK-7Q. ...'
$output = Join-Path $artifactDir "$model-$effort.txt"
$timing = Join-Path $artifactDir "$model-$effort.time.txt"

$mcpConfigJson = & copilot.cmd mcp list --json
if ($LASTEXITCODE -ne 0) {
    throw "Configured MCP servers could not be enumerated."
}
$mcpConfig = $mcpConfigJson | ConvertFrom-Json
$disabledMcpArgs = @(
    $mcpConfig.mcpServers.PSObject.Properties.Name |
        ForEach-Object { "--disable-mcp-server=$_" }
)

$sw = [Diagnostics.Stopwatch]::StartNew()
& copilot.cmd -C . `
  --model $model `
  --effort $effort `
  --prompt $prompt `
  --available-tools view glob grep lsp `
  --allow-all-tools `
  --deny-tool=write `
  --deny-tool='shell(*)' `
  --disable-builtin-mcps `
  @disabledMcpArgs `
  --no-custom-instructions `
  --no-remote `
  --no-remote-export `
  --no-auto-update `
  --max-ai-credits 120 `
  1> $output 2>&1
$exitCode = $LASTEXITCODE
$sw.Stop()
"elapsed_ms=$($sw.ElapsedMilliseconds)`nexit_code=$exitCode" | Set-Content $timing
```

`--max-ai-credits`は比較対象と予算に合わせる。1回のmodel callで上限を超える場合があるため、厳密なhard capとは扱わない。

### 公平性

- wall-clock比較が目的なら同時実行しない。逐次実行し、順番をrunごとに入れ替える。
- 品質・費用比較だけなら並列実行可能だが、rate limitやホスト負荷の影響を注記する。
- output statsから必ず `AI Credits`、input、cached input、cache write、output、reasoning tokens、elapsedを取得する。
- token数とAI Creditsは別指標として扱う。モデル間で単純なtoken単価換算を推測しない。

## 採点

source/testを読み、モデルの自己評価に依存せず100点で採点する。

| 評価軸 | 点 |
| --- | ---: |
| 6つの客観項目の事実正確性・網羅性 | 45 |
| `file:line`の正確性・具体性 | 20 |
| リスク指摘の正しさ・重要性 | 15 |
| 指示遵守・構造 | 10 |
| 簡潔さ・signal-to-noise | 10 |

### 採点ルール

- 重要な誤り、early returnや例外経路の欠落、存在しないtest保証は大きく減点する。
- 正しいが粗い行範囲、repo相対でないpath、別statementを指すlineは引用点を減点する。
- style指摘、推測だけの問題、無理に作った問題はrisk点を与えない。
- marker欠落やprompt破損は品質採点から分離し、成功率へ反映する。
- 可能なら候補に含まれない高性能modelをjudgeとして使い、judge model、effort、採点promptを記録する。
- judgeの判定もsource/testへspot-checkし、誤採点をそのまま採用しない。

## 集計

結果表には最低限以下を含める。

| Model | Effort | Success | Quality | AI Credits | Input | Cached | Cache write | Output | Reasoning | Elapsed |
| --- | --- | :-: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |

3run時は次を示す。

- 成功率
- 品質の中央値と範囲
- AI Creditsの中央値と範囲
- elapsedの中央値と範囲
- 各modelの最重要な誤りまたは欠落

「score / AI Credits」は参考値に留める。低価格modelほど大量tokenを使えるため、これだけで品質効率を断定しない。

## 推奨の決め方

- **既定model:** 成功率が十分高く、品質が最高modelから小差で、AI Creditsが明確に低いもの。
- **高難度model:** 複雑な制御フロー、設計、監査、重大障害で最高品質のもの。
- **軽量model:** 定型修正、検索、要約で必要品質を満たす最安のもの。
- effort差が品質2点以内なら低いeffortを既定にする。高いeffortは、そのrunで実際に改善した観点を必要とする作業に限定する。
- preview modelはGA modelと同点でも既定にせず、安定性とpolicy availabilityを確認する。

## 最終報告

1. 先に結論を示す。
2. 実測表を示す。
3. 品質差を具体的な正解・誤りで説明する。
4. 成功率とn数を明示する。
5. 既定、高難度、軽量の使い分けを提案する。
6. 公式のモデル用途・価格ページを参考として付ける。
7. artifact保存先を示すが、機密コードやprompt/outputを外部へ送信しない。

## Issueへの記録

ベンチマーク結果をIssueへ保存する場合は、次の順序を必ず守る。

1. `.github/ISSUE_TEMPLATE/model-benchmark.yml` の項目に沿って、aggregate結果だけを含むIssueタイトルと本文案を作る。
2. タイトルには比較した全modelまたはeffortを明記する。
3. raw prompt、model出力、session artifact、ローカルartifact保存先はIssueへ記載しない。
4. `model-benchmark` ラベルを付ける。ラベルが存在せず作成権限もない場合は、必要なラベル名、説明、推奨色をユーザーへ伝える。
5. Issueを作成する前に、タイトル、本文、ラベルの下書きをユーザーへ提示し、`ask_user` で承認を得る。承認前にIssueを作成しない。
6. 承認後にIssueを作成する。修正を求められた場合は下書きを直し、再度承認を得る。

## 今回の静的コード理解ベンチマーク基準

2026-08-10、RepoSyncRadarのMorning Triageを各3run、`high` / default contextで比較した基準値。実行時HEADは`2720f8b0ebc36f67836790abe3cb1ef97b1ea0c7`、Copilot CLIは`1.0.79-9`。worktreeにはこのPRのmodel・設定・Skill変更が未コミットで存在した。数値は中央値、括弧内はmin-max:

| Model | Success | Quality | AI Credits | Input | Cached | Cache write | Output | Reasoning | Elapsed |
| --- | :-: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| GPT-5.6 Sol | 3/3 | 96 (95-98) | 76.4 (74.2-90.5) | 357.3k (326.8-551.7k) | 281.4k (251.4-467.6k) | 75.9k (75.4-84.1k) | 4.9k (4.8-5.0k) | 1.6k (1.4-1.7k) | 2m50s (2m36s-3m01s) |
| GPT-5.6 Luna | 3/3 | 94 (92-94) | 4.71 (4.27-5.56) | 727.7k (543.0-935.0k) | 621.0k (443.6-821.2k) | 106.6k (99.3-113.7k) | 7.5k (6.7-8.9k) | 3.8k (3.0-4.0k) | 2m44s (2m13s-2m45s) |
| GPT-5.6 Terra | 3/3 | 89 (88-91) | 43.9 (34.6-62.4) | 628.1k (412.5k-1.1m) | 524.0k (323.1-971.9k) | 104.0k (89.4-135.0k) | 6.2k (4.8-7.7k) | 1.7k (1.6-2.1k) | 2m41s (2m31s-3m41s) |

固定ルーブリックのjudgeはClaude Opus 5 / high。Solは制御フローと例外処理の精度が最も高く、Lunaは2点差でAI Credits中央値が約16分の1、TerraはLunaより低品質かつ約9倍のAI Creditsだった。この結果では既定をLuna / high、高難度用途をSol / highとする。

Lunaのeffort比較は`high`、`max`ともに各1runの参考値であり、成功率、中央値、範囲を評価できない:

| Effort | Quality | AI Credits | Input | Output | Reasoning | Elapsed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| high | 88 | 5.22 | 891.3k | 8.5k | 4.1k | 3m01s |
| max | 89 | 5.99 | 738.6k | 15.9k | 11.3k | 3m11s |

上記のモデル間3run比較は静的コード理解の一次評価であり、Morning Triageの採点品質を直接測ったものでも、将来のmodelを恒久的に順位付けするものでもない。新モデル評価時は同じ手法で再測定し、既定値変更前に代表的なSDKワークフローでも妥当性を確認する。
