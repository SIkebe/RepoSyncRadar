# RepoSyncRadar — 設計ドキュメント

> 本書は、GitHub Enterprise Cloud 管理者が `github/docs` の Repo sync PR を日次で確認し、自分の管理・運用している環境に影響しうる product / policy / billing / security / operational change を見つけるためのデスクトップアプリ **RepoSyncRadar** の設計を、議論の経緯ごと記録するものです。
>
> - **言語 / ランタイム**: C# (.NET 10) / WPF + BlazorWebView
> - **エージェント**: [`github/copilot-sdk`](https://github.com/github/copilot-sdk) の .NET SDK (`GitHub.Copilot.SDK`)
> - **データ**: SQLite + EF Core
> - **GitHub 連携**: Octokit.NET
> - **公式 Docs 連携**: `docs.github.com/api/*`

---

## 目次

1. [課題と目的](#1-課題と目的)
2. [ペルソナ別検討と相互レビュー](#2-ペルソナ別検討と相互レビュー)
3. [全体アーキテクチャ](#3-全体アーキテクチャ)
4. [技術選定の根拠](#4-技術選定の根拠)
5. [GitHub Copilot SDK の正しい理解](#5-github-copilot-sdk-の正しい理解)
6. [カスタムツール一覧](#6-カスタムツール一覧)
7. [セッション設計](#7-セッション設計)
8. [権限ハンドラ / フック / 監査](#8-権限ハンドラ--フック--監査)
9. [docs.github.com 連携](#9-docsgithubcom-連携)
10. [データモデル](#10-データモデル)
11. [UI / UX](#11-ui--ux)
12. [ローカルプレビュー戦略](#12-ローカルプレビュー戦略)
13. [`github/docs` 取り込み方針](#13-githubdocs-取り込み方針)
14. [セキュリティと運用](#14-セキュリティと運用)
15. [プロジェクト構成](#15-プロジェクト構成)
16. [Phase 別ロードマップ](#16-phase-別ロードマップ)
17. [意思決定ログ](#17-意思決定ログ)
18. [今後の検討事項](#18-今後の検討事項)

---

## 1. 課題と目的

### 1.1 現状の負担構造

ユーザーの日次運用は単一作業ではなく **5 層のパイプライン** で発生している。

| レイヤー | 行為 | 負担の正体 |
|---|---|---|
| L1 取得 | Repo sync PR を開く | 受動的 / 量が多い |
| L2 走査 | 全コミットメッセージを読む | 反復・退屈・見落としリスク |
| L3 判定 | 「紹介すべきか」を判断 | 文脈・経験依存、暗黙知 |
| L4 検証 | 差分を確認する | 認知負荷高、長い差分も含む |
| L5 出力 | 顧客向け / 短文共有に書く | 媒体ごとに語調・粒度が異なる |

### 1.2 達成したいこと(ゴール)

- L1〜L5 のうち、L1 / L2 を自動化、L3 / L5 を補助、L4 を可視化する。
- **「1 日 5〜10 分」** で 80% カバーできるワークフローを実現する。
- 過去の判断(注目 / 保留 / 見送り候補 / アーカイブ / 無視ディレクトリ / 重要度ブースト / 文体)をデータとして蓄積し、毎週ツールが賢くなる。
- ファイルパス → 公開 URL の対応が常に画面上で見える。
- 公式ドキュメントとレンダリング上区別がつかない見た目で差分を検証できる。

### 1.3 やらないこと(非ゴール)

- 自動投稿は行わない。最終判断と投稿は人間が行う。
- 公式の GitHub Changelog、リリースノート、サポート案内を置き換える機能ではない。管理者レビューの補助。
- モバイル対応は現時点ではスコープ外(将来 MAUI Blazor で再利用可能)。
- 多言語対応(日本語のみ。英訳は共有文案の中で副次的に生成)。

---

## 2. ペルソナ別検討と相互レビュー

5 つのペルソナで案を出し、相互レビューを経て統合した。

### 2.1 各ペルソナの提案要旨

| ペルソナ | 立場 | 主張 |
|---|---|---|
| **Akira** (DevOps / 自動化) | ルールベース至上主義 | GitHub Actions + YAML 設定の決定論パイプラインで L1/L2 を全部潰す。watch / ignore / must-notify を分け、Issue ダイジェスト出力 |
| **Lisa** (AI / LLM) | エージェント志向 | 3 段階 LLM(cheap → medium → expensive)。Stage 1 で全件スコアリング、Stage 2 で要約、Stage 3 で注目後のみ共有文案。レビュー判断を JSONL で蓄積し few-shot 化 |
| **Maya** (PM / 時短) | ワークフロー設計 | 朝 5 分トリアージのタイムボックス設計。Must read 5 件上限、Skim 15 件、Archive 無制限。KPI(紹介数 / 反応 / 見逃し率)で運用評価 |
| **Hiro** (DevRel / マーケ) | 出力起点 | 媒体ごとに要件が違う前提で、媒体別テンプレートを LLM プロンプトと一体化(YAML)。「Why I cared」を注目時に 1 行残して文体学習材料に |
| **Sarah** (UX / プロダクティビティ) | 体験起点 | Tinder for commits のスワイプ UI / マルチデバイス(PWA / VS Code 拡張 / Teams bot)。状態を 1 つの SQLite に集約、Issue とも双方向同期 |

### 2.2 相互レビューで全員が合意した「最低限の柱」

1. **データ取得は決定論的に** (GitHub Actions または cron + `gh` CLI)。
2. **判定は LLM 主体だが、ルールで 1 次フィルタしてコストと暴走を抑える**。
3. **学習ループ(レビュー判断のフィードバック)を初日から組み込む**。
4. **UI は段階的に拡張、初期は Issue + Teams bot で十分**。
5. **出力テンプレートは LLM プロンプトと同一資産として管理する**。

### 2.3 全員が警告した「アンチパターン」

- ❌ 全コミットを LLM で要約 → コスト爆発、ノイズ増幅。
- ❌ 最初から完璧な UI → 何ヶ月も使えないまま放置される。
- ❌ 自動投稿 → 信頼を一度に失う。**人間の最終確認は必ず残す**。
- ❌ ローカル環境依存 → 出張・スマホで詰む。**状態はクラウドまたは可搬な DB に持つ**。

### 2.4 デスクトップアプリへの方針転換

上記レビュー後、ユーザーから「差分の公式レンダリング表示 / ファイルパス→URL のマッピング表示 / 注目・保留・見送り候補・アーカイブ・無視ディレクトリ・未確認のデータ蓄積」が必須要件と提示され、クライアントアプリ(Blazor Desktop / WPF)へ全面転換。さらに **GitHub Copilot SDK** を中核に据える方針が確定。

---

## 3. 全体アーキテクチャ

```
┌──────────────────────────────────────────────────────────────────────┐
│  WPF Shell (RepoSyncRadar.exe)                                       │
│  ┌─────────────────────────┐  ┌─────────────────────────────────┐   │
│  │ BlazorWebView (UI)      │  │ WebView2 (docs.github.com)      │   │
│  │  - Razor Components     │  │  - /api/article/body の表示     │   │
│  │  - MudBlazor            │  │  - 公式 URL の埋め込み          │   │
│  └─────────────────────────┘  └─────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │ .NET Core Services (in-process)                              │   │
│  │  - CopilotAgentService  (GitHub.Copilot.SDK)                 │   │
│  │  - GitHubClient          (Octokit.NET)                       │   │
│  │  - DocsApiClient         (HttpClient → docs.github.com)      │   │
│  │  - PathToUrlResolver     (frontmatter + pagelist)            │   │
│  │  - RadarStore            (Microsoft.Data.Sqlite + EF Core)   │   │
│  │  - PreviewCoordinator    (Markdown/Liquid local preview)     │   │
│  │  - SecretStore           (DPAPI / Windows Credential Manager)│   │
│  └──────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────┘
        │ HTTPS                          │ JSON-RPC (stdio)
        ▼                                ▼
┌─────────────────────┐           ┌────────────────────────┐
│ GitHub API          │           │ copilot CLI (bundled)  │
│ docs.github.com APIs│           │  ↳ Agent + Tools + MCP │
└─────────────────────┘           └────────────────────────┘
```

ポイント:

- UI (Razor) とコアサービスは **同一プロセス**。Electron/Node のような IPC を作り込む必要がない。
- Copilot CLI は SDK にバンドルされ、**stdio 経由で 1 プロセス起動**。`await using var client = new CopilotClient()` で完結。
- 公式 docs の見た目は **2 つの WebView 経路**(BlazorWebView 内の iframe / 別 WebView2 ペイン)で実現する。

---

## 4. 技術選定の根拠

### 4.1 Blazor Desktop の選択肢比較

| 方式 | 概要 | 採用 |
|---|---|---|
| **WPF + BlazorWebView** | WPF ウィンドウに `BlazorWebView` を埋め込み、UI は Razor。Windows 専用 | ★★★ **採用** |
| .NET MAUI Blazor Hybrid | クロスプラットフォーム(Win/Mac/iOS/Android) | ★★ 将来クロスプラットフォーム化する可能性があれば |
| Photino.Blazor | OS WebView 直接利用、軽量だが native 周りは自前 | ★ |
| WinForms + BlazorWebView | UI 機能が貧弱 | × |

#### WPF + BlazorWebView を選ぶ理由

- **Windows 単独利用**前提で MAUI のクロスプラットフォーム税を払う意味が薄い。
- 「左に差分(Razor)」「右に `docs.github.com` 埋め込み(別 WebView2)」のような **マルチペイン** を素直に組める。
- `Microsoft.Web.WebView2.Wpf` で公式サイト埋め込みが 1 行。
- システムトレイ、グローバルホットキー、ファイル監視など Windows ネイティブ機能との相性が抜群。
- `BlazorWebView` の Razor コンポーネントは MAUI と同じ — 後で MAUI 移植が可能。

### 4.2 Electron / Tauri を選ばなかった理由

| 項目 | Electron | Tauri | WPF + Blazor |
|---|---|---|---|
| 言語 | JS/TS | Rust + JS | **C# + Razor** |
| バンドルサイズ | 100〜250 MB | 10〜30 MB | 中(ランタイム同梱次第) |
| Node エコシステム | フル利用可 | sidecar 必要 | 不要(.NET エコシステムで完結) |
| 学習コスト | 低 | 中(Rust) | **ユーザーの本職** |
| Windows ネイティブ統合 | 良 | 良 | **最良** |

ユーザーは C# が一番得意 → **言語の優位を最大化する選択**。

### 4.3 その他のスタック

| 領域 | 採用 | 理由 |
|---|---|---|
| データ | SQLite + EF Core | ローカル単独 / 軽量 / クエリ容易 / バックアップが 1 ファイル |
| GitHub API | Octokit.NET | .NET の事実上標準 |
| 公式 docs データ取得 | HttpClient → `docs.github.com/api/*` | 自前パースが不要、redirect 解決も API 側でしてくれる |
| エージェント | `GitHub.Copilot.SDK` | 後述。Copilot CLI 全機能を JSON-RPC で駆動 |
| Razor UI | MudBlazor | Fluent UI Blazor でも可。差分表示は `BlazorMonaco` を併用 |
| Git 操作 (Phase 6) | `git` CLI | bare クローンから SHA 指定で必要ファイルを読む |
| 自動更新 | Velopack | Squirrel.Windows の後継 |
| シークレット | DPAPI / Windows Credential Manager | OS 標準 |

---

## 5. GitHub Copilot SDK の正しい理解

### 5.1 誤解と訂正

| 旧認識 | 正解 |
|---|---|
| OpenAI / GitHub Models API への薄いラッパ | **Copilot CLI(エージェント本体)を JSON-RPC で駆動する SDK** |
| 単発の Chat Completion を呼ぶ | **セッション**を作り、エージェントが**自律的に計画・ツール呼び出し・ファイル編集**を行う |
| プロンプトとレスポンスのみ | `SessionEvent` ストリーム(User / Assistant / ToolStart / ToolComplete / Idle / Error / Delta…)+ Streaming + Hooks + 権限承認 |
| LLM 呼び出しは自前で組む | **オーケストレーションは Copilot 側、自分は「ツール」と「権限ハンドラ」を実装** |
| Node / Python のみ | **.NET 公式サポート(`GitHub.Copilot.SDK` NuGet)** |
| 課金は Models API | **Copilot サブスクリプションのプレミアムリクエスト**(BYOK で OpenAI / Anthropic / Azure AI Foundry も可) |

### 5.2 SDK の構造

- **`CopilotClient`** — Copilot CLI プロセスのライフサイクル管理。`StartAsync` / `StopAsync` / `CreateSessionAsync` / `ResumeSessionAsync` / `ListSessionsAsync` / `DeleteSessionAsync` / `PingAsync`。
- **`CopilotSession`** — 単一会話セッション。`SendAsync` / `AbortAsync` / `On(handler)` / `GetMessagesAsync` / `DisposeAsync`。
- **イベント** — `UserMessageEvent`, `AssistantMessageEvent`, `AssistantMessageDeltaEvent`, `AssistantReasoningEvent`, `ToolExecutionStartEvent`, `ToolExecutionCompleteEvent`, `SessionStartEvent`, `SessionIdleEvent`, `SessionErrorEvent`, `SessionCompactionStartEvent`, `SessionCompactionCompleteEvent`。
- **必須ハンドラ** — `OnPermissionRequest`(全セッションで必須)。
- **任意ハンドラ** — `OnUserInputRequest`(`ask_user` ツール用)、`OnElicitationRequest`(フォーム UI 提供)。
- **Hooks** — `OnPreToolUse` / `OnPostToolUse` / `OnUserPromptSubmitted` / `OnSessionStart` / `OnSessionEnd` / `OnErrorOccurred`。
- **System Message Modes** — `Append`(推奨、ガードレール保持)/ `Customize`(セクション単位の上書き)/ `Replace`(全置換、危険)。
- **Tools** — `Microsoft.Extensions.AI.AIFunctionFactory.Create` で型安全に定義。`AdditionalProperties` で `is_override` / `skip_permission` を制御可。
- **Slash commands** — `CommandDefinition` で `/myCmd` を定義し TUI から呼べる(本アプリでは未使用予定)。
- **Telemetry** — OpenTelemetry / file exporter。`System.Diagnostics.Activity` を活用、`traceparent` 自動伝搬。
- **Infinite Sessions** — デフォルト ON。context 上限を自動 compaction + workspace ディレクトリに永続化。`SessionCompactionStartEvent` / `SessionCompactionCompleteEvent` で観測可能。
- **BYOK** — `ProviderConfig` で `Type` / `BaseUrl` / `ApiKey` 指定。Entra ID / Managed Identity は不可、API キーのみ。
- **認証** — アプリ同梱の公開 OAuth Client ID + Device Flow / DPAPI 保存トークン / デバッグ用環境変数 (`COPILOT_GITHUB_TOKEN`) / BYOK。

### 5.3 必須事項チェックリスト

- [x] **`OnPermissionRequest` を必ず実装する**(Approve / Deny / Custom)。`PermissionHandler.ApproveAll` は使わない。
- [x] **`await using` でクライアント / セッションを破棄する**(`DisposeAsync` 必須)。
- [x] **`SessionIdleEvent` で完了を待つ**(`TaskCompletionSource` パターン)。
- [x] **`SystemMessageMode.Append` を基本**にしてガードレールを残す。
- [x] **イベントは switch / pattern matching で型安全に**振り分ける。
- [x] **エラーは `SessionErrorEvent` と `StreamJsonRpc.RemoteInvocationException` の両方**を処理する。

---

## 6. カスタムツール一覧

Copilot エージェントは「計画 → ツール選択 → 実行 → 観察 → 反復」を自走する。我々は **アプリ固有のツールを C# で書き、`SessionConfig.Tools` に登録** する。

| ツール名 | 役割 | 主な実装 |
|---|---|---|
| `radar_list_commits` | Repo sync PR の未確認コミット一覧 | SQLite + Octokit |
| `radar_get_diff` | 指定 SHA の差分(全文 or 部分) | Octokit (`pullRequests.listFiles`) |
| `radar_resolve_url` | リポジトリ内パス → 公開 URL 一覧 | pagelist API + frontmatter versions |
| `radar_fetch_rendered` | `/api/article/body` で公式 HTML 取得 | HttpClient |
| `radar_score_commit` | スコアと注目カテゴリを保存 | SQLite (`Scoring`) |
| `radar_save_review` | 注目 / 保留 / 見送り候補 / アーカイブ / Seen の保存 | SQLite (`Review`) |
| `radar_post_draft` | 媒体別の共有文案を保存 | SQLite (`Draft`) |
| `radar_ignore_rule` | 無視ディレクトリ / パターンの追加 | SQLite (`IgnoreRule`) |
| `radar_boost_rule` | 重要度ブーストルールの追加 | SQLite (`BoostRule`) |

### 6.1 ツール定義の例

```csharp
using Microsoft.Extensions.AI;
using System.ComponentModel;

public static AIFunction CreateResolveUrlTool(PathToUrlResolver resolver) =>
    AIFunctionFactory.Create(
        async ([Description("Repository-relative path (e.g. content/copilot/.../foo.md)")] string repoPath,
               [Description("Frontmatter `versions:` block text")] string frontmatterVersions) =>
        {
            var urls = await resolver.ResolveAsync(repoPath, frontmatterVersions);
            return new { repoPath, urls };
        },
        "radar_resolve_url",
        "Resolve a docs repo file path to its canonical docs.github.com URLs across versions.");
```

### 6.2 ツール選定方針

- **副作用ありのツールは `OnPermissionRequest` で必ず確認**(`radar_save_review` / `radar_post_draft` / `radar_ignore_rule` 等)。
- **読み取り専用ツールは `skip_permission = true` 可**(`radar_list_commits` / `radar_get_diff` 等)。

---

## 7. セッション設計

### 7.1 `MorningTriageSession`

- 朝の一括処理。「最新の Repo sync PR を取り込み、スコアリング → 要約 → Must read 5 件を選出」。
- モデル: `gpt-5` 既定(コスト最適)。`Streaming = true`。
- `SystemMessageMode.Append` で日本語の運用ルール(無視リスト、ブースト、媒体特性)を投入。

### 7.2 `AdoptionSession`

- ユーザーが注目したコミット 1 件に対し、Twitter / 顧客向けの下書きを生成。
- モデル: `claude-sonnet-4.5` を選好(文体表現力)。
- 入力: 注目コミット + 差分 + 解決済み URL + 過去の注目例 5 件(few-shot)。
- 出力: JSON で `{ explanation, twitter, customer }`。

### 7.3 `MaintenanceSession`(任意 / 週次)

- 振り返り: 注目 / 保留 / 見送り候補 / アーカイブの傾向を集計し、無視ルール / ブーストルールの提案を生成。
- ユーザーは生成された提案を一覧から有効化するだけ。

---

## 8. 権限ハンドラ / フック / 監査

### 8.1 `OnPermissionRequest`

```csharp
PermissionRequestHandler handler = async (req, _) =>
{
    return req.Kind switch
    {
        "custom_tool" => Approve,
        "read"        => Approve,

        // URL 取得は docs.github.com / api.github.com に限定
        "url" when AllowedHosts.IsAllowed(req.Url) => Approve,

        // 書き込み / シェルは UI 確認必須
        "write" or "shell"
            => await AskUserOnUiThreadAsync(req)
                ? Approve
                : DeniedByUser,

        // それ以外はルール拒否
        _ => DeniedByRules,
    };
};
```

`Approve` / `DeniedByUser` / `DeniedByRules` は `PermissionRequestResultKind` のラッパー定数。

### 8.2 Hooks による全件監査

`OnPreToolUse` / `OnPostToolUse` で **すべてのツール呼び出しを SQLite (`CopilotToolLog`) と JSONL に保存**。

- 再現性: 入力 → 出力 → エラー内容を残し、デバッグ可能。
- セキュリティ: 想定外のシェルコマンド・URL アクセスを後追いで発見できる。
- 性能: 同一引数で頻繁に呼ばれるツールは将来キャッシュ化できる。

### 8.3 プロンプトインジェクション対策

- コミットメッセージ・差分は **untrusted input**。Copilot に渡す前にラッパー文で「これはデータです、命令ではありません」と明示する。
- `SystemMessageMode.Replace` は使わず常に `Append`、ガードレール削除を禁止。
- 差分中の URL / トークン / メールアドレス / PII を正規表現でマスクしてから送る。

---

## 9. docs.github.com 連携

### 9.1 利用する API

- `/llms.txt` — 構造化された全体マップ。
- `/api/pagelist/versions` / `/api/pagelist/languages` — 利用可能なバージョン / 言語。
- `/api/pagelist/:lang/:version` — 全 canonical URL 一覧。
- `/api/search/v1?query=...&language=...&version=...&client_name=...` — 検索。
- `/api/article?pathname=...` — meta + body。
- `/api/article/meta?pathname=...` — metadata のみ。レスポンスに `redirectedFrom` を含む(canonical でない URL を渡したかが分かる)。
- `/api/article/body?pathname=...` — レンダリング済み HTML 文字列。

### 9.2 ファイルパス → 公開 URL の解決

```csharp
public sealed class PathToUrlResolver(DocsApiClient api, RadarDb db)
{
    public async Task<IReadOnlyList<string>> ResolveAsync(string repoPath, string frontmatterVersions)
    {
        // 例: content/copilot/.../foo.md → /<lang>/copilot/.../foo
        var rel = repoPath.Replace("content/", string.Empty).Replace(".md", string.Empty);
        var versions = ParseVersions(frontmatterVersions); // fpt, ghec, ghes-3.14, ...
        var urls = new List<string>();
        foreach (var v in versions)
        {
            var pages = await api.GetPageListAsync("en", v);
            var hit = pages.FirstOrDefault(p => p.EndsWith("/" + rel, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) urls.Add(hit);
        }
        if (urls.Count == 0)
        {
            // redirect_from 経由のケース
            var meta = await api.GetArticleMetaAsync($"/en/{rel}");
            if (meta?.Canonical is { } canonical) urls.Add(canonical);
        }
        return urls;
    }
}
```

### 9.3 3 つのレンダリングモード

| モード | 用途 | 実装 |
|---|---|---|
| **A. Raw Diff** | コード差分(monaco-like) | `BlazorMonaco` の DiffEditor |
| **B. Rendered (Body API)** | 本番 docs と同じ HTML | `BlazorWebView` 内の iframe (`srcdoc`) で `/api/article/body` の応答を流し込む |
| **C. Live Site** | 公式ページそのもの | `Microsoft.Web.WebView2.Wpf` を別ペインに配置、`Source = "https://docs.github.com/<path>"` |

B のメリットは「採用前のコミットでも HTML 部分だけは公式と同じパイプラインで描画される」こと。C は外部リソース込みの最終的な見た目を確認する用。

---

## 10. データモデル

```csharp
public sealed class Commit
{
    public string Sha { get; set; } = default!;
    public int PrNumber { get; set; }
    public string Message { get; set; } = default!;
    public string Author { get; set; } = default!;
    public DateTime AuthoredAt { get; set; }
    public DateTime FetchedAt { get; set; }
    public List<CommitFile> Files { get; set; } = new();
    public Scoring? Scoring { get; set; }
    public Review? Review { get; set; }
    public List<Draft> Drafts { get; set; } = new();
}

public sealed class CommitFile
{
    public string Sha { get; set; } = default!;
    public string Path { get; set; } = default!;
    public string Status { get; set; } = default!; // added/modified/removed/renamed
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

public sealed class Scoring
{
    public string Sha { get; set; } = default!;
    public double Score { get; set; }
    public string Category { get; set; } = default!;
    public string AudienceJson { get; set; } = "[]";
    public string SummaryJa { get; set; } = string.Empty;
    public string WhyJa { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public DateTime ScoredAt { get; set; }
}

public enum ReviewStatus { Unseen, Seen, Adopted, Rejected, Archived, Later }

public sealed class Review
{
    public string Sha { get; set; } = default!;
    public ReviewStatus Status { get; set; } = ReviewStatus.Unseen;
    public string? Reason { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public sealed class Draft
{
    public int Id { get; set; }
    public string Sha { get; set; } = default!;
    public string Channel { get; set; } = default!; // twitter/customer/explanation
    public string Body { get; set; } = default!;
    public bool Posted { get; set; }
    public string? PostedUrl { get; set; }
    public DateTime GeneratedAt { get; set; }
}

public sealed class PathUrlMap
{
    public string Path { get; set; } = default!;
    public string Version { get; set; } = default!;
    public string Language { get; set; } = default!;
    public string Url { get; set; } = default!;
    public DateTime ResolvedAt { get; set; }
}

public sealed class IgnoreRule
{
    public string Pattern { get; set; } = default!;
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BoostRule
{
    public string Pattern { get; set; } = default!;
    public double Delta { get; set; }
    public string? Reason { get; set; }
}

public sealed class CopilotToolLog
{
    public int Id { get; set; }
    public string SessionId { get; set; } = default!;
    public string ToolName { get; set; } = default!;
    public string ArgsJson { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
}
```

---

## 11. UI / UX

### 11.1 メイン画面のレイアウト

```
┌───────────────────────────────────────────────────────────────────────┐
│ ☰  RepoSyncRadar              [Sync ↻] [Copilot ✦ 自然言語] [Settings]│
├──────────────┬────────────────────────────────────────────────────────┤
│ Sidebar      │ Workbench                                              │
│              │                                                        │
│ ▼ Today      │ ┌─ Commit Header ────────────────────────────────────┐ │
│   🔴 5 unseen│ │ feat(copilot): clarify ... │ 3 files │ +42 −5     │ │
│   🟡 12 skim │ │ URL: /en/copilot/.../about-copilot  [↗]            │ │
│ ▼ This week  │ │ Versions: fpt, ghec, ghes-3.14, ghes-3.15          │ │
│   ☑ 38 adopt │ │ Score: 0.72  Category: feature-update              │ │
│   ◇ 64 skip  │ └────────────────────────────────────────────────────┘ │
│   ☒ 18 done  │                                                        │
│              │ ┌─ Tabs: [Diff] [Rendered ▼] [Why] [Drafts] [History]┐ │
│ Filters      │ │  [ ◀ Before │ After ▶ ]   [ Open in browser ↗ ]    │ │
│ ☑ content/   │ │                                                    │ │
│ ☐ data/      │ │  ← Live HTML from /api/article/body                │ │
│ ☑ release    │ │  → Local Markdown preview (Before / After)        │ │
│              │ └────────────────────────────────────────────────────┘ │
│              │                                                        │
│              │ [ 注目 ✓ ]  [ 保留 ⏰ ] [ アーカイブ ] [ Ignore dir ] │
└──────────────┴────────────────────────────────────────────────────────┘
```

### 11.2 ワークフロー状態

| 状態 | UI | 内部 |
|---|---|---|
| 未確認 | Sidebar 🔴 | `Review.Status = Unseen` |
| 既読(レガシー) | Sidebar には出さず未確認へ合算 | `Seen` |
| 注目 | 見逃さず追いたい候補 | `Adopted` |
| 保留 | 保留キュー、翌朝に繰り越し | `Later` |
| 見送り候補 | Triage / Ignore が低優先度と判断 | `Rejected` |
| アーカイブ | アクティブな確認対象から外す | `Archived` + `Reason` 入力 |
| 無視ディレクトリ | 右上「このディレクトリを無視」 | `IgnoreRule` 追加 + 関連未確認を一括 `Rejected(reason="auto-ignored")` |
| 重要度ブースト | ファイル右クリックメニュー | `BoostRule` 追加、score に加算 |

### 11.3 Drafts パネル

```
┌─ Drafts (generated by Copilot) ─────────────────────────────────────┐
│ [Twitter 🇯🇵]                                              [Copy] [↗]│
│  GitHub Docs に Copilot Workspace の新しい挙動が反映されました。... │
│                                                                     │
│ [顧客向け]                                                [Copy]    │
│  対象: GHES 3.15 / 機能: ... / お客様アクション: ...                │
│                                                                     │
│ [Regenerate ✦] (feedback: なぜこれにした?)                          │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 12. ローカルプレビュー戦略

### 12.1 まずは API オンリーで進める

| 必要なデータ | API オンリーで取れる? |
|---|---|
| PR とコミット一覧 | ✅ Octokit |
| 各コミットの変更ファイル / patch | ✅ Octokit |
| frontmatter (versions) | ✅ Octokit でファイル取得 |
| pagelist と URL 解決 | ✅ `docs.github.com/api/pagelist/...` |
| Body API (公式の見た目) | ✅ `docs.github.com/api/article/body` |
| **PR HEAD でローカル Markdown/Liquid プレビュー** | ❌ ローカル bare clone 必要 |

Phase 1〜5 は **完全に API のみ**で進められる。

### 12.2 Phase 6 で必要になったら bare クローン + Markdown/Liquid renderer

```
c:\github\
├─ docs\                      ← 既存の作業ツリー(触らない)
├─ docs.git\                  ← bare クローン(RepoSyncRadar 専用、読み取り)
├─ docs-preview-cache\        ← Markdown preview assets / legacy cleanup root
└─ RepoSyncRadar\             ← アプリ本体
```

初期セットアップ:

```powershell
git clone --bare https://github.com/github/docs.git C:\github\docs.git
cd C:\github\docs.git
git config --add remote.origin.fetch '+refs/pull/*/head:refs/pull/*/head'
git fetch --prune
```

`appsettings.json`:

```jsonc
{
  "DocsRepository": {
    "BareCloneDir": "C:\\github\\docs.git",
    "WorktreeRoot": "C:\\github\\docs-worktrees",
    "CloneUrl": "https://github.com/github/docs.git",
    "PreviewBasePort": 5055
  }
}
```

`DocsWorktreeManager` の責務:

- 指定 SHA の Markdown / Liquid / asset 入力を bare clone から `git show` / `git ls-tree` で読む。
- 必要な静的 asset だけを preview cache に materialize する。
- 旧バージョンが残した worktree と stale Next dev server を cleanup する。
- `BareCloneDir` が未設定なら **Phase 6 機能をオフ** にしてアプリは動く。

---

## 13. `github/docs` 取り込み方針

### 13.1 submodule は使わない

| 問題 | 詳細 |
|---|---|
| 目的の不一致 | submodule は特定 SHA を vendoring する用途。毎日最新を追う今回とは正反対 |
| サイズ | `github/docs` は大きい。アプリリポジトリの clone / CI が重くなる |
| PR HEAD の取得が面倒 | `refs/pull/N/head` の fetch は submodule の特性と無関係 |
| worktree との二重管理 | 後述の bare clone + worktree 戦略を submodule 上で組むのは煩雑 |

### 13.2 採用方針

- **アプリは新規リポジトリ(`c:\github\RepoSyncRadar`)として独立**。
- **Phase 1〜5: GitHub API + docs.github.com API オンリー**。
- **Phase 6 でローカルプレビューが必要になったら bare クローン + Markdown/Liquid renderer を追加**。
- アプリと `github/docs` の作業ツリーは完全に分離 — 既存の `c:\github\docs` を一切汚さない。

---

## 14. セキュリティと運用

| 項目 | 対策 |
|---|---|
| GitHub OAuth トークン管理 | DPAPI(`CurrentUser`) で暗号化してローカル保存。通常利用で PAT は使わない |
| プロンプトインジェクション | コミットメッセージ / 差分はデータとしてラップ、`SystemMessageMode.Append` でガードレール維持 |
| ツール権限 | `OnPermissionRequest` で `shell` / `write` / 任意 `url` は UI ダイアログ必須。`ApproveAll` は使わない |
| シークレットの LLM 送信防止 | 差分の URL / トークン / メアド / PII を正規表現でマスクしてから送る |
| ログ | `OnPreToolUse` / `OnPostToolUse` 全件を SQLite と JSONL に保存 |
| アプリ署名 | `signtool.exe` + 自前 EV 証明書、または Microsoft Store 配布 |
| 自動更新 | Velopack(`dotnet` 由来、公開鍵検証付き) |
| 通信先制限 | `docs.github.com` / `api.github.com` / `models.github.ai` / `localhost`(プレビュー)のみ |

---

## 15. プロジェクト構成

```
RepoSyncRadar.sln
├─ docs/
│   └─ DESIGN.md                    ← 本書
├─ src/
│   ├─ RepoSyncRadar.App/           ← WPF 起動アセンブリ(.NET 8 windows)
│   │   ├─ App.xaml / App.xaml.cs
│   │   ├─ MainWindow.xaml / MainWindow.xaml.cs
│   │   ├─ Components/              ← 初期段階の Razor コンポーネント
│   │   │   ├─ _Imports.razor
│   │   │   └─ Workbench.razor
│   │   └─ wwwroot/                 ← Blazor static assets
│   └─ RepoSyncRadar.Core/          ← モデル / DbContext / オプション / サービス IF
│       ├─ Data/
│       │   └─ RadarDbContext.cs
│       ├─ Models/
│       │   └─ DomainModels.cs
│       ├─ Options/
│       │   ├─ GitHubOptions.cs
│       │   ├─ DocsApiOptions.cs
│       │   └─ CopilotOptions.cs
│       └─ Services/
│           ├─ IGitHubClient.cs
│           ├─ IDocsApiClient.cs
│           └─ ICopilotAgent.cs
├─ .editorconfig
├─ .gitignore
├─ Directory.Build.props
├─ Directory.Packages.props
├─ README.md
└─ global.json
```

**初期は 2 プロジェクト**(App + Core)で開始。Phase 2 以降で `Integrations` / `Ui` / `Tests` を切り出す。

### 15.1 主要 NuGet(`Directory.Packages.props`)

```xml
<PackageVersion Include="Microsoft.AspNetCore.Components.Web" Version="..." />
<PackageVersion Include="Microsoft.AspNetCore.Components.WebView.Wpf" Version="..." />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="..." />
<PackageVersion Include="Microsoft.Extensions.AI" Version="..." />
<PackageVersion Include="Microsoft.Extensions.Hosting" Version="..." />
<PackageVersion Include="Microsoft.Extensions.Options" Version="..." />
<PackageVersion Include="Microsoft.Web.WebView2" Version="..." />
<PackageVersion Include="MudBlazor" Version="..." />
<PackageVersion Include="GitHub.Copilot.SDK" Version="..." />
<PackageVersion Include="Octokit" Version="..." />
```

バージョン番号は `dotnet outdated` で最新を確認のうえ確定。

---

## 16. Phase 別ロードマップ

| Phase | 内容 | 完了基準 |
|---|---|---|
| **0. 雛形** | WPF + BlazorWebView + EF Core SQLite + Octokit のスキャフォールド、DESIGN.md | `dotnet build` が通り、ウィンドウが起動して "Hello" 表示 |
| **1. ドキュメント取得 / 表示** | Octokit で Repo sync PR コミット一覧、`PathToUrlResolver`、`/api/article/body` 連携、WebView2 右ペイン | 1 クリックで「コミット / URL / 公式の見た目」が並んで見える |
| **2. Copilot SDK 統合** | `CopilotClient` 起動、最小ツール (`radar_list_commits` / `radar_get_diff`) 登録、Morning Triage セッション | スコアと要約が SQLite に入る |
| **3. レビュー UI** | 注目 / 保留 / アーカイブ / Ignore、Reason 入力、Sidebar フィルタ | 1 日 5 分運用が成立 |
| **4. 共有文案** | Adoption セッション + 2 媒体テンプレート + Regenerate | 注目 → 文案 → 編集 → クリップボードで完結 |
| **5. ローカルプレビュー** | bare clone + Markdown/Liquid renderer + local content server、Before/After 並列表示 | PR HEAD の見た目で比較可能 |
| **6. 配布・運用** | 署名済み配布、更新、プライバシー/サポート/脆弱性報告導線 | 管理者が安心してインストールできる |

Phase 0〜2 で **既に大幅に負担軽減**、Phase 4 で 80% カバー、Phase 5 でプレビューまで完結。

---

## 17. 意思決定ログ

| # | 決定 | 日付 | 経緯 |
|---|---|---|---|
| D1 | デスクトップアプリとして実装 | 2026-05-12 | L3 / L4 がブラウザ + GitHub UI では効率悪い |
| D2 | Blazor Desktop (WPF + BlazorWebView) を採用 | 2026-05-12 | Windows 専用 / C# 優位 / マルチペイン構成 / MAUI 移植余地 |
| D3 | Electron / Tauri は不採用 | 2026-05-12 | C# 単一スタックの優位を最大化 |
| D4 | `GitHub.Copilot.SDK` を中核に据える | 2026-05-12 | エージェントオーケストレーションを自前実装しない |
| D5 | SQLite + EF Core を採用 | 2026-05-12 | ローカル単独、バックアップ 1 ファイル、クエリ容易 |
| D6 | submodule は不採用、アプリは独立リポジトリ | 2026-05-12 | submodule は vendoring 用途 / 毎日追跡と相性が悪い |
| D7 | Phase 1〜4 は API オンリー、Phase 5 で bare clone + Markdown/Liquid preview | 2026-05-12 | 初期セットアップを軽量に保つ |
| D8 | `SystemMessageMode.Append` を基本とする | 2026-05-12 | ガードレール削除を禁止 |
| D9 | `OnPermissionRequest` で `shell` / `write` / 任意 `url` を UI 確認必須 | 2026-05-12 | `ApproveAll` は使わない |
| D10 | 自動投稿はしない | 2026-05-12 | 信頼を失わないため、最終確認は人間 |

---

## 18. 今後の検討事項

- [ ] **モデル選定の自動化**: コスト / 品質 / レイテンシで動的に選ぶか、設定で固定か。
- [ ] **多言語下書きの英訳併走**: Twitter 用に日本語と英語を並行生成するか。
- [ ] **Teams bot との連携**: 注目 / アーカイブを Teams のチャットボタンから操作できるようにするか。
- [ ] **Copilot Extension への昇格**: アプリを Copilot Chat から `@reposync` で呼べるようにするか。
- [ ] **MAUI Blazor 移植**: iPad / スマホで参照したくなった場合の Razor 共有率を見積もる。
- [ ] **トレーニングデータの外部化**: 注目例 few-shot を別リポジトリに公開して他社事例を取り込めるか。
- [ ] **`docs.github.com` 以外の対応**: 同様のリポジトリ(`microsoft/azure-docs` 等)に拡張可能か。
- [ ] **ベンチマーク**: 朝のトリアージ完了までの所要時間を KPI 化する。
- [ ] **GHES リリースノートの優先扱い**: `data/release-notes/**` は常時 high boost。

---

## 付録 A — 参考リンク

- [`github/copilot-sdk`](https://github.com/github/copilot-sdk) — Multi-platform SDK
- [.NET README](https://github.com/github/copilot-sdk/blob/main/dotnet/README.md)
- [C# Instructions](https://github.com/github/awesome-copilot/blob/main/instructions/copilot-sdk-csharp.instructions.md)
- [`github/docs` article API](https://github.com/github/docs/blob/main/src/article-api/README.md)
- [Copilot CLI](https://github.com/features/copilot/cli)
- [Velopack](https://velopack.io/)
- [MudBlazor](https://mudblazor.com/)

## 付録 B — 用語集

| 用語 | 意味 |
|---|---|
| **Repo sync PR** | GitHub 内部リポジトリと `github/docs` の同期 PR。日次で多数のコミットが入る |
| **Adoption** | 注目したコミットを顧客向け / 短文共有で紹介すると決めること |
| **Triage** | コミットを未確認から注目候補 / 見送り候補へ振り分けること |
| **Boost** | 重要度スコアに加算するルール |
| **BYOK** | Bring Your Own Key。OpenAI / Anthropic / Azure AI Foundry を Copilot SDK 経由で使うこと |
| **canonical URL** | docs.github.com の正規 URL(redirect 先) |
| **Body API** | `/api/article/body?pathname=...`。レンダリング済み HTML を返す |
| **pagelist** | `/api/pagelist/:lang/:version`。全 canonical URL の一覧 |
