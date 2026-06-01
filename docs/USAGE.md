# RepoSyncRadar 使い方ガイド

GitHub Enterprise Cloud 管理者が `github/docs` の Repo sync PR を確認し、自分の管理・運用している環境に影響しうる product / policy / billing / security / operational change を見つけるための Windows デスクトップアプリです。公式 GitHub Changelog やサポート案内を置き換えるものではなく、Changelog に明示されない docs 由来のシグナルを 5〜10 分でレビューできる状態に圧縮します。

設計の出発点は [DESIGN.md](DESIGN.md)、ステップ別の実装範囲は [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) を参照してください。

---

## 目次

1. [前提環境](#1-前提環境)
2. [初期セットアップ](#2-初期セットアップ)
3. [画面の構成](#3-画面の構成)
4. [毎日のワークフロー](#4-毎日のワークフロー510-分)
5. [オプション機能](#5-オプション機能)
6. [自動更新](#6-自動更新)
7. [既知の制約と運用 Tips](#7-既知の制約と運用-tips)
8. [ビルド・テストのワンライナー](#8-ビルドテストのワンライナー)
9. [さらに深く知るには](#9-さらに深く知るには)

---

## 1. 前提環境

| 項目 | バージョン / 条件 |
|---|---|
| OS | Windows 11(WebView2 ランタイム必須、通常はプリインストール済) |
| .NET SDK | .NET 10 SDK 以降([global.json](../global.json) で固定) |
| GitHub Copilot | アクティブな Copilot サブスクリプション(初回起動時に Copilot CLI が自動取得される) |
| GitHub アカウント | Copilot を有効化したアカウント(初回起動時にデバイスフローでサインイン) |
| GitHub OAuth App | 通常は配布版に同梱された RepoSyncRadar 公式 OAuth App を使用。組織管理や fork では任意で上書き可能 |

---

## 2. 初期セットアップ

### 2.1 取得 〜 ビルド

```powershell
git clone <repo-url> C:\github\RepoSyncRadar
cd C:\github\RepoSyncRadar
dotnet restore
dotnet build
```

### 2.2 OAuth 設定

RepoSyncRadar は **アプリ上でサインインさせた GitHub ユーザーアカウントの OAuth トークン** を Copilot SDK に渡します(PAT は使いません)。一般配布版には RepoSyncRadar 公式 OAuth App の **Client ID** が同梱されているため、通常は GitHub OAuth App を自分で作成する必要はありません。

`OAuthClientId` は公開識別子であり、トークンや client secret ではありません。配布物に含めても認証情報の漏えいにはあたりません。取得後の OAuth アクセストークンだけが秘密情報で、DPAPI でローカル保存されます。

組織ポリシー、社内配布、fork、検証環境などで独自 OAuth App を使いたい場合だけ、以下の手順で上書きしてください。

1. https://github.com/settings/developers → **OAuth Apps** → **New OAuth App**
2. 必須フィールドを埋める(Application name は任意 / Homepage URL は任意の URL / Authorization callback URL は `http://localhost/` でよい — Device Flow では使われない)
3. 作成後の設定画面で **Enable Device Flow** にチェックを入れて保存(これを忘れると `/login/device/code` が HTTP 422 で失敗します)
4. 画面に表示される **Client ID** をコピー(`Iv23li...` のような文字列)
5. シークレットは **発行しない**(Device Flow では不要)

### 2.3 設定ファイルを書く

[src/RepoSyncRadar.App/appsettings.json](../src/RepoSyncRadar.App/appsettings.json) は **コミット済みの既定値** です。一般配布版ではそのまま起動できます。環境固有の値や独自 OAuth App の Client ID だけ、開発時は同フォルダの `appsettings.local.json`、インストール版は `%LocalAppData%\RepoSyncRadar\appsettings.local.json` に追記します。

```jsonc
{
   "GitHub": {
      // 任意: この日時以降に作成された Repo sync PR だけを Morning Triage の対象にする。
      // null / 未指定なら作成日では絞り込まない。
      "PullRequestCreatedAtOrAfter": "2026-05-15T00:00:00Z"
   },
  "Copilot": {
    "DefaultModel": "gpt-5",
      "LogLevel": "info",
      "SessionIdleTimeoutSeconds": 0,
      "TelemetryFilePath": "",
      "CaptureContent": false,
    "AllowedUrlHosts": [ "docs.github.com", "api.github.com" ],
      // 任意: 公式配布 Client ID を使う場合は省略。独自 OAuth App の場合だけ指定。
      "OAuthClientId": "Iv23liXXXXXXXXXXXXXX",
    "OAuthScopes": [ "public_repo" ]
  },
  // Step 19 を使う場合のみ。空ならローカルプレビュー機能はオフ。
  "DocsRepository": {
    "BareCloneDir": "C:\\github\\.cache\\docs.git",
    "CloneUrl": "https://github.com/github/docs.git",
    "WorktreeRoot": "C:\\github\\.cache\\docs-worktrees",
    "MaxWorktrees": 5,
    "PreviewCommand": "npm",
    "PreviewArguments": "run dev -- --port {port}",
    "PreviewInstallArguments": "install",
      "PreviewEnvironment": {
         "PORT": "{port}",
         "REQUEST_TIMEOUT": "600000"
      },
    "PreviewBasePort": 4500
  }
}
```

> `Copilot.OAuthClientId` は GitHub Copilot SDK にも、`github/docs` の PR を読む Octokit にも **同じ OAuth ユーザートークン** を渡すために使います。PAT (`ghp_...`) の設定はもう不要です。一般配布版では公式 Client ID が既定値です。独自 OAuth App を使う場合だけ `appsettings.local.json` または環境変数 `RADAR_Copilot__OAuthClientId` で上書きしてください。
>
> `OAuthScopes` には `public_repo` を指定してください — Copilot SDK の認証(ユーザー識別)と Octokit の `github/docs` 読み取りの両方を 1 つのトークンでまかなえます。
>
> **Copilot SDK 診断**: `LogLevel` は SDK が起動する Copilot CLI のログレベルです。`TelemetryFilePath` を指定すると SDK の OpenTelemetry file exporter を有効化します。通常は `CaptureContent: false` のままにしてください。`SessionIdleTimeoutSeconds` は `0` / 未指定なら SDK 既定(無効)です。
>
> **トークンの保管場所**: OAuth で取得したアクセストークンは DPAPI(`CurrentUser` スコープ)で暗号化し `%LocalAppData%\RepoSyncRadar\github-token.bin` に保存されます。ヘッダーの **Sign out** で保存済みトークンを削除できます。手動でこのファイルを削除しても、次回起動時に再サインインを求められます。
>
> **デバッグ override**: 環境変数 `COPILOT_GITHUB_TOKEN` を立てると OAuth フローを省略してその値を Copilot SDK / Octokit に渡します(`GH_TOKEN` / `GITHUB_TOKEN` のような汎用 PAT 変数は意図的に **読まない** — 他ツールのトークンの誤用を防ぐため)。

起動後はヘッダー右側の **設定** から、ローカル `appsettings.local.json` の `GitHub` / `DocsApi` / `Copilot` / `DocsRepository` / `Logging` / `Updates` の値を表示・変更できます。保存した内容はローカル設定ファイルに書き戻され、次回起動時に確実に反映されます。インストール版の保存先はアプリ更新で差し替わらない `%LocalAppData%\RepoSyncRadar\appsettings.local.json` です。同じ設定パネルで、直接参照している NuGet パッケージのサードパーティ ライセンスも確認できます。

設定パネルの **Copilot 使用量** では、SDK の usage event / session metrics が返す AI Credits を優先して表示します。SDK が AI Credits を返さず、モデル名と token breakdown だけが得られる場合は、GitHub Docs の [Models and pricing for GitHub Copilot](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing) にある per 1M token 単価から概算します。公式価格表にないモデルは、誤った見積もりを避けるため `credits 未報告` のままにします。

### 2.4 起動

```powershell
dotnet run --project src/RepoSyncRadar.App
```

初回起動時:

1. `%LocalAppData%\RepoSyncRadar\radar.db` に SQLite を作成し、EF Core マイグレーションが走る
2. **GitHub Device Flow サインインダイアログ** が前面に出る:
   - ユーザーコード(`ABCD-1234` 形式)が自動でクリップボードにコピーされる
   - 既定ブラウザが `https://github.com/login/device` を自動で開く
   - 開いたページでコードを貼り付けて GitHub にサインイン → 「Authorize」をクリック
   - アプリ側はポーリングで完了を検知し、トークンを DPAPI で保存してダイアログを閉じる
3. Copilot CLI がバンドルから展開・常駐(子プロセス)
4. WPF + BlazorWebView の本体ウィンドウが開く

> 2 回目以降は保存済みトークンを使うのでサインイン UI は出ません。GitHub 側でセッションを取り消した場合のみ再サインインを求められます。

---

## 3. 画面の構成

```
┌──────────────┬──────────────────────────────────────────────┐
│ Sidebar      │ Workbench                                    │
│  未確認       │  ┌──────────────────────────────────────┐    │
│  注目         │  │ Commit List  (絞り込み済みの一覧)    │    │
│  保留         │  ├──────────────────────────────────────┤    │
│  見送り候補   │  │ Commit Detail                        │    │
│  アーカイブ   │  │   - Files / URLs / scoring details   │    │
│              │  │   - Review Actions (Focus/Archive/…) │    │
│              │  │   - Drafts Panel  (2 媒体下書き)     │    │
│              │  └──────────────────────────────────────┘    │
└──────────────┴──────────────────────────────────────────────┘
```

- **Sidebar**: ステータス別の件数。クリックでフィルタ切り替え。
- **Commit List**: 取り込まれたコミットの一覧。クリックで詳細へ。
- **Commit Detail**: ファイル一覧 + 公開 URL マッピング。

---

## 4. 毎日のワークフロー(5〜10 分)

### 4.1 Triage

[`MorningTriageSession`](../src/RepoSyncRadar.App/Copilot/MorningTriageSession.cs) が以下を順に実行します(Step 15)。

画面上部でサインイン済みになっていることを確認し、**Triage** ボタンを押します。

1. `github/docs` の Repo sync PR を最大 `MaxPullRequests` 件取得
   - `GitHub:PullRequestCreatedAtOrAfter` を設定している場合は、その日時以降に作成された PR だけを対象にします
2. 各 PR のコミットを SQLite に **冪等取り込み**(既知 SHA はスキップ)
3. Copilot に「上位候補を未確認に残し、明らかに不要なものは見送り候補へ送る」方針でスコアリング
4. `radar_score_commit` で `Scoring` テーブルに要約・理由・スコアを保存
5. 見送り判定が明確なものは `radar_save_review` で `Reviews.Status = Rejected` として保存

> 起動直後はサイドバーの **未確認** に並びます。

### 4.2 レビュー(注目 / 保留 / 見送り候補 / アーカイブ / Ignore)

Commit List で 1 件選び、[`ReviewActions`](../src/RepoSyncRadar.App/Components/ReviewActions.razor) から処理します(Step 16):

| アクション | 用途 | データ |
|---|---|---|
| **注目** | 見逃さず追いたい候補 | `Reviews.Status = Adopted` |
| **保留** | 判断をいったん止めて残す | `Reviews.Status = Later` |
| **アーカイブ** | アクティブな確認対象から外す | `Reviews.Status = Archived`, `Reviews.Reason` に保存 |
| **Ignore Directory** | このパス配下を以降まとめて見送り候補にする | `IgnoreRules` 追加 + 既存も一括 `Rejected` |

`Rejected` は Triage や無視ルールで自動分類された見送り候補、`Archived` はユーザーが手動でアクティブな確認対象から外したアーカイブ状態です。
`Ignore Directory` は `EF.Functions.Like` で `path/%` を一括処理するので、不要なディレクトリを 1 クリックで見送り候補へ送れます。

### 4.3 共有文案(Drafts Panel)

注目したコミットでは、必要に応じて [`DraftsPanel`](../src/RepoSyncRadar.App/Components/DraftsPanel.razor) から媒体別の共有文案を確認できます(Step 17)。

- 既に文案があれば即表示。なければ **Regenerate** ボタンで生成。
- [`AdoptionSession`](../src/RepoSyncRadar.App/Copilot/AdoptionSession.cs) が、差分(50KB 超は安全に切り詰め) + 過去 5 件の注目例(few-shot)を Copilot に渡し、Twitter / 顧客向けの 2 媒体を JSON で返させて `Drafts` テーブルに保存。
- 各媒体には **コピーボタン**(WPF Dispatcher 経由で Clipboard へ)。
- 注目キューで複数コミットをチェックすると、複数の差分を横断した **まとめて解説生成** が使えます。結果は選択セット向けの一時テキストとして Workbench に表示し、必要に応じてコピーできます。

## 5. オプション機能

### 5.1 ローカルプレビュー(Step 19 / 19.5)

`DocsRepository` セクションを埋めておくと、PR HEAD の見た目を bare clone + worktree で確認できます。空のままなら **完全に no-op** で他機能には影響しません。起動時に bare clone を事前作成したい場合だけ `DocsRepository:PrewarmOnStartup` を `true` にしてください。既定では、初回起動だけで `github/docs` の大きな clone/fetch は始まりません。

#### 使い方

1. コミット一覧から PR を選び、右下の **「ローカルプレビュー」** ボタンを押す
2. 内部で次が自動で走る:
   - 初回のみ `git clone --bare <DocsRepoUrl> <BareCloneDir>`
   - `git fetch origin +refs/pull/{PR}/head:...` で PR head を取得
   - `git rev-parse <sha>^` で変更前の親コミットを解決
   - `git worktree add <WorktreeRoot>/<parent-sha> <parent-sha>` と `git worktree add <WorktreeRoot>/<sha> <sha>` で変更前 / PR HEAD の作業ディレクトリを生成
   - worktree に `node_modules` が無ければ `npm install` を自動実行
   - `PreviewBasePort` から空いている連続 2 port を探し、PR HEAD と変更前の sidecar として起動(同じ SHA / 同じ port に対しては再起動しない)
3. 右側の WebView2 が左右 2 ペインになり、左に変更前 localhost、右に PR HEAD localhost を表示します。読み込み完了後、変更された本文ブロックは左側に取り消し線、右側に淡いハイライトで表示されます。公式 `docs.github.com` が既に更新済みでも、コミット単位の見た目差分を確認できます
4. ボタン下に表示されている URL は外部ブラウザでも開けます

#### 前提

- Node.js + npm が PATH に通っていること(`PreviewCommand="npm"` を上書きすれば他ツールでも可)
- worktree に `node_modules` が無い場合、`PreviewServerHost` が `PreviewCommand` + `PreviewInstallArguments` (既定: `npm install`) を自動実行してから preview server を起動します
- `github/docs` の初回 Next.js コンパイルは 15 秒を超えることがあるため、`REQUEST_TIMEOUT=600000` を preview sidecar に渡します。これを短くすると PR HEAD 側が `Service Unavailable` になることがあります
- WebView2 の URL allow-list は `https` のみ通すデフォルトに加え、`PreviewSession` がプレビュー中に割り当てた `http://localhost:<port>` だけを動的に許可します。前回プロセスの異常終了などで `PreviewBasePort` が既に使われている場合は、次の空き port にずらします。同じ worktree で古い Next dev server が残っている場合も、起動前に `.next/dev/logs/next-development.log` の PID を確認して停止し、`Another next dev server is already running` の再発を防ぎます

#### node_modules の扱い (案 A / 案 B)

worktree ごとに作業ディレクトリが分かれるため、`npm install` の置き場所を選ぶ必要があります。**案 B (junction で共有)** をアプリが自動で行うようになったため、通常は手動の設定は不要です。

- **案 A — 各 worktree で個別に `npm install`** (フォールバック・確実):
   1. アプリが `node_modules` 不在を検知したら自動で `npm install` を実行
   2. 初回 5〜15 分・1〜2 GB 消費。worktree を削除すれば一緒に消えます
- **案 B — `<WorktreeRoot>/.shared-node-modules/<hash>` に 1 度だけ `npm install` → 各 worktree から junction で共有** (既定・高速・ディスク節約):
   1. `NodeModulesShareManager` が `package-lock.json` の SHA-256 短ハッシュをスロット ID に使い、`<WorktreeRoot>/.shared-node-modules/<hash>/node_modules` を 1 度だけ作成
   2. 各 worktree の `node_modules` は `cmd /c mklink /J` で共有スロットへの directory junction
   3. `next dev` の watch は junction を透過するため、PR head を切り替えるたびに `node_modules` を再生成しなくて済みます (2 回目以降の起動は数秒)
   4. junction 作成に失敗した場合や package-lock.json が見つからない場合は自動的に案 A にフォールバックします
   - 注意: `package.json` の依存が PR 内で書き換わっている (= `package-lock.json` のハッシュが変わっている) と新しいスロットが切られて再 install されます。これは期待動作です

#### キャッシュ (worktree) のクリーンアップ

放置すると `<WorktreeRoot>` 配下に PR head ごとの作業ディレクトリが溜まり続けます (1 件あたり 1〜2 GB)。次のいずれかで一括削除できます:

- **アプリ内**: プレビューパネルの **「キャッシュをクリーンアップ」** ボタン
   - 起動時に `git worktree list --porcelain` で既存 worktree を再ハイドレートしているため、前回プロセスで作られたものも含めて削除されます
- **CLI**: `pwsh ./scripts/Clean-Worktrees.ps1` (確認だけしたい場合は `-WhatIf`)
   - 既定で `appsettings.local.json → appsettings.json` の順に `DocsRepository:BareCloneDir` を読みます。明示指定したい場合は `-BareCloneDir <path>`

#### 仕組み

- [`DocsWorktreeManager`](../src/RepoSyncRadar.Core/Services/Preview/DocsWorktreeManager.cs) が `git clone --bare` した親リポから変更前 / PR HEAD の worktree を切り、[`PreviewServerHost`](../src/RepoSyncRadar.Core/Services/Preview/PreviewServerHost.cs) がそれぞれ `npm run dev` を sidecar 起動
- [`PreviewCoordinator`](../src/RepoSyncRadar.Core/Services/Preview/PreviewCoordinator.cs) が clone → fetch → parent 解決 → checkout → start → `PreviewSession.Activate(port...)` を 1 ステップで束ねます
- [`PreviewPathMapper`](../src/RepoSyncRadar.Core/Services/Preview/PreviewPathMapper.cs) が `content/foo/bar.md` を `/en/foo/bar` に、`content/index.md` を `/en` に変換します
- LRU で `MaxWorktrees` を超えたら最も古い worktree を `git worktree remove --force`
- アプリ終了時に preview プロセスは確実に kill されます(`IAsyncDisposable`)

### 5.2 監査ログ

すべての `radar_*` ツール呼び出しは `CopilotToolLogs` テーブルに記録(Step 12 の `OnPreToolUse` / `OnPostToolUse`)。

---

## 6. 自動更新

インストーラー版の RepoSyncRadar は Velopack で配布され、同じチャンネルに新しいリリースが公開されるとアプリ起動時に更新を確認できます。開発ビルドを `dotnet run` で起動している場合は自動更新の対象外です。

### 6.1 利用者が知っておくこと

- 自動更新は既定では控えめな動作です。更新が見つかるとバックグラウンドでダウンロードし、実行中の作業セッションを強制終了しません。
- ダウンロード中はヘッダーに進捗が表示されます。ダウンロードが完了すると、ヘッダーに今すぐ再起動するか後で再起動するかの確認が表示されます。
- `Restart now` を選ぶと Velopack が更新を適用してアプリを再起動します。`Later` を選んだ場合、ダウンロード済み更新は次回起動時に適用されます。
- 更新元はチャンネル単位です。通常利用は `win-x64-stable` または `win-arm64-stable` を使い、検証版だけ `beta` / `preview` 系チャンネルを使います。
- 企業・組織内で独自配布する場合は、配布元の GitHub Release または静的ファイル置き場に、インストーラーだけでなく `.nupkg` と `releases.<channel>.json` / `assets.<channel>.json` を同じ更新フィードとして配置してください。

### 6.2 設定

配布版では release defaults で設定します。ローカル検証や社内配布で上書きしたい場合は、開発時はプロジェクト直下、インストール版は `%LocalAppData%\RepoSyncRadar\appsettings.local.json` の `Updates` セクションを編集します。

```jsonc
{
   "Updates": {
      "Enabled": true,
      "CheckOnStartup": true,
      "FeedUrl": "https://github.com/<owner>/<repo>",
      "Channel": "win-x64-stable",
      "CheckTimeoutSeconds": 120
   }
}
```

| 設定 | 用途 |
|---|---|
| `Enabled` | 更新確認機能そのものを有効化します。公開更新フィードが確定するまでは無効にできます。 |
| `CheckOnStartup` | アプリ起動時に更新確認します。無効なら手動確認 UI / 将来の運用導線だけで確認します。 |
| `FeedUrl` | Velopack 更新フィードの場所です。GitHub Releases を使う場合はリポジトリ URL を指定します。 |
| `Channel` | 受け取るリリース系列です。CPU アーキテクチャと配布段階を合わせます。 |
| `CheckTimeoutSeconds` | 更新確認のタイムアウトです。ネットワークが遅い環境では長めにします。 |

配布・リリース作成側の詳細仕様は [RELEASE.md](RELEASE.md) を参照してください。

---

## 7. 既知の制約と運用 Tips

| 項目 | 状態 |
|---|---|
| 自動投稿 | **しない方針**(下書きまで。最終確認は人間) |
| 多言語 | 日本語のみ。英訳は媒体下書きの中で副次的に出るのみ |
| クラウド同期 | なし。`radar.db` を持ち運ぶ運用 |
| Velopack 自己更新 | インストーラー版のみ対象。開発ビルドは `git pull && dotnet build` で更新 |
| GitHub 認証 | OAuth Device Flow で 1 度サインイン → DPAPI 暗号化トークンを `%LocalAppData%\RepoSyncRadar\github-token.bin` に保管。Copilot SDK と Octokit (`github/docs` 読み取り) で同じトークンを共有。 |

---

## 8. ビルド・テストのワンライナー

```powershell
# 厳格ビルド(警告=エラー)
dotnet build -warnaserror

# 自動テスト(手動カテゴリ除く)
dotnet test --no-build -- --filter-not-trait Category=Manual

# 一発スクリプト
.\scripts\check.ps1
```

---

## 9. さらに深く知るには

- 設計の出発点と意思決定ログ → [DESIGN.md](DESIGN.md)
- 配布・自動更新の詳細仕様 → [RELEASE.md](RELEASE.md)
- ステップ別の実装範囲・テスト件数 → [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)
- Razor コンポーネントの構造 → [../src/RepoSyncRadar.App/Components/](../src/RepoSyncRadar.App/Components/)
- Copilot セッション層 → [../src/RepoSyncRadar.App/Copilot/](../src/RepoSyncRadar.App/Copilot/)
