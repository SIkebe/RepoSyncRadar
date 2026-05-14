# RepoSyncRadar 使い方ガイド

`github/docs` の Repo sync PR を毎朝さばいて SNS / 社内 / 顧客向け Changelog を発信する負担を、5〜10 分のワークフローに圧縮するための Windows デスクトップアプリです。Step 1〜19 まで実装されており、Copilot CLI が手元で動く環境があれば実用できます。

設計の出発点は [DESIGN.md](DESIGN.md)、ステップ別の実装範囲は [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) を参照してください。

---

## 目次

1. [前提環境](#1-前提環境)
2. [初期セットアップ](#2-初期セットアップ)
3. [画面の構成](#3-画面の構成)
4. [毎日のワークフロー](#4-毎日のワークフロー510-分)
5. [オプション機能](#5-オプション機能)
6. [既知の制約と運用 Tips](#6-既知の制約と運用-tips)
7. [ビルド・テストのワンライナー](#7-ビルドテストのワンライナー)
8. [さらに深く知るには](#8-さらに深く知るには)

---

## 1. 前提環境

| 項目 | バージョン / 条件 |
|---|---|
| OS | Windows 11(WebView2 ランタイム必須、通常はプリインストール済) |
| .NET SDK | .NET 10 SDK 以降([global.json](../global.json) で固定) |
| GitHub Copilot | アクティブな Copilot サブスクリプション(初回起動時に Copilot CLI が自動取得される) |
| GitHub アカウント | Copilot を有効化したアカウント(初回起動時にデバイスフローでサインイン) |
| GitHub OAuth App | **コントリビューターが用意する OAuth App(後述 2.2)。Device Flow 有効化必須** |

---

## 2. 初期セットアップ

### 2.1 取得 〜 ビルド

```powershell
git clone <repo-url> C:\github\RepoSyncRadar
cd C:\github\RepoSyncRadar
dotnet restore
dotnet build
```

### 2.2 GitHub OAuth App を作成して Device Flow を有効化

RepoSyncRadar は **アプリ上でサインインさせた GitHub ユーザーアカウントの OAuth トークン** を Copilot SDK に渡します(PAT は使いません)。最初に一度だけ OAuth App を用意してください。

1. https://github.com/settings/developers → **OAuth Apps** → **New OAuth App**
2. 必須フィールドを埋める(Application name は任意 / Homepage URL は任意の URL / Authorization callback URL は `http://localhost/` でよい — Device Flow では使われない)
3. 作成後の設定画面で **Enable Device Flow** にチェックを入れて保存(これを忘れると `/login/device/code` が HTTP 422 で失敗します)
4. 画面に表示される **Client ID** をコピー(`Iv23li...` のような文字列)
5. シークレットは **発行しない**(Device Flow では不要)

### 2.3 設定ファイルを書く

[src/RepoSyncRadar.App/appsettings.json](../src/RepoSyncRadar.App/appsettings.json) は **コミット済みの既定値** です。OAuth App の Client ID など環境固有の値は、同フォルダに `appsettings.Local.json` を作って追記します(`.gitignore` 済み)。

```jsonc
{
  "Copilot": {
    "DefaultModel": "gpt-5",
    "AllowedUrlHosts": [ "docs.github.com", "api.github.com" ],
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
    "PreviewBasePort": 4500
  }
}
```

> `Copilot.OAuthClientId` は GitHub Copilot SDK にも、`github/docs` の PR を読む Octokit にも **同じ OAuth ユーザートークン** を渡すために使います。PAT (`ghp_...`) の設定はもう不要です。
>
> `OAuthScopes` には `public_repo` を指定してください — Copilot SDK の認証(ユーザー識別)と Octokit の `github/docs` 読み取りの両方を 1 つのトークンでまかなえます。
>
> **トークンの保管場所**: OAuth で取得したアクセストークンは DPAPI(`CurrentUser` スコープ)で暗号化し `%LocalAppData%\RepoSyncRadar\github-token.bin` に保存されます。サインアウトはサイドバーから(Step 21 で UI 追加予定)、あるいはこのファイルを削除すれば次回起動時に再サインインを求められます。
>
> **デバッグ override**: 環境変数 `COPILOT_GITHUB_TOKEN` を立てると OAuth フローを省略してその値を Copilot SDK / Octokit に渡します(`GH_TOKEN` / `GITHUB_TOKEN` のような汎用 PAT 変数は意図的に **読まない** — 他ツールのトークンの誤用を防ぐため)。

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
│  Inbox       │  ┌──────────────────────────────────────┐    │
│  Adopted     │  │ Ask Palette  (Ctrl+Enter で実行)     │    │
│  Rejected    │  ├──────────────────────────────────────┤    │
│  Later       │  │ Commit List  (絞り込み済みの一覧)    │    │
│  Ignored     │  ├──────────────────────────────────────┤    │
│              │  │ Commit Detail                        │    │
│              │  │   - Review Actions (Adopt/Reject/…)  │    │
│              │  │   - Drafts Panel  (3 媒体下書き)     │    │
│              │  └──────────────────────────────────────┘    │
└──────────────┴──────────────────────────────────────────────┘
```

- **Sidebar**: ステータス別の件数。クリックでフィルタ切り替え。
- **Ask Palette**: 自然言語で SQL を投げるコマンドパレット(Step 18 で追加)。
- **Commit List**: 取り込まれたコミットの一覧。クリックで詳細へ。
- **Commit Detail**: ファイル一覧 + 公開 URL マッピング。

---

## 4. 毎日のワークフロー(5〜10 分)

### 4.1 朝のトリアージ(Morning Triage)

[`MorningTriageSession`](../src/RepoSyncRadar.App/Copilot/MorningTriageSession.cs) が以下を順に実行します(Step 15)。

1. `github/docs` の Repo sync PR を最大 `MaxPullRequests` 件取得
2. 各 PR のコミットを SQLite に **冪等取り込み**(既知 SHA はスキップ)
3. Copilot に「Must read 5 件 / Skim 15 件 / 残りは Archive」の方針でスコアリング
4. `radar_save_review` を呼んで `Reviews` テーブルに保存

> 起動直後はサイドバーの **Inbox** に並びます。

### 4.2 レビュー(Adopt / Reject / Later / Ignore)

Commit List で 1 件選び、[`ReviewActions`](../src/RepoSyncRadar.App/Components/ReviewActions.razor) から処理します(Step 16):

| アクション | 用途 | データ |
|---|---|---|
| **Adopt** | 採用、媒体別下書き候補 | `Reviews.Status = Adopted` |
| **Reject** | 紹介しない理由を 1 行残す | `Reviews.Message` に保存 |
| **Later** | 後回し | `Reviews.Status = Later` |
| **Ignore Directory** | このパス配下を以降全部除外 | `IgnoreRules` 追加 + 既存も一括 Reject |

`Ignore Directory` は `EF.Functions.Like` で `path/%` を一括処理するので、不要なディレクトリを 1 クリックで永続的に視界から外せます。

### 4.3 媒体別下書き(Drafts Panel)

Adopt したコミットを選ぶと [`DraftsPanel`](../src/RepoSyncRadar.App/Components/DraftsPanel.razor) が表示されます(Step 17)。

- 既に下書きがあれば即表示。なければ **Regenerate** ボタンで生成。
- [`AdoptionSession`](../src/RepoSyncRadar.App/Copilot/AdoptionSession.cs) が、差分(50KB 超は安全に切り詰め) + 過去 5 件の採用例(few-shot)を Copilot に渡し、Twitter / Teams / 顧客向けの 3 媒体を JSON で返させて `Drafts` テーブルに保存。
- 各媒体には **コピーボタン**(WPF Dispatcher 経由で Clipboard へ)。

### 4.4 Ask Palette(自然言語フィルタ)

Workbench 最上段の [`AskPalette`](../src/RepoSyncRadar.App/Components/AskPalette.razor)(Step 18):

1. 「先週採用したコミットで `actions` ディレクトリのものは?」のように日本語で書く
2. **実行** か **Ctrl+Enter** で送信
3. [`AskSession`](../src/RepoSyncRadar.App/Copilot/AskSession.cs) が Copilot に SQL を作らせ、[`SqlGuard`](../src/RepoSyncRadar.Core/Services/SqlGuard.cs) で検査(SELECT のみ / 許可 9 テーブル / `LIMIT 100` 強制)
4. 通過した SQL を読み取り専用 SQLite 接続で実行 → Markdown 表で表示

`SQL を表示 (debug)` にチェックを入れると、実行された SQL も出ます(デバッグ用途)。

---

## 5. オプション機能

### 5.1 ローカルプレビュー(Step 19 / 19.5)

`DocsRepository` セクションを埋めておくと、PR HEAD の見た目を bare clone + worktree で確認できます。空のままなら **完全に no-op** で他機能には影響しません。

#### 使い方

1. コミット一覧から PR を選び、右下の **「ローカルプレビュー」** ボタンを押す
2. 内部で次が自動で走る:
   - 初回のみ `git clone --bare <DocsRepoUrl> <BareCloneDir>`
   - `git fetch origin +refs/pull/{PR}/head:...` で PR head を取得
   - `git worktree add <WorktreeRoot>/<sha> <sha>` で作業ディレクトリを生成
   - worktree に `node_modules` が無ければ `npm install` を自動実行
   - `npm run dev -- --port {port}` を sidecar として起動(同じ SHA に対しては再起動しない)
3. 右側の WebView2 が `http://localhost:{port}/{言語}/{パス}` に切り替わり、変更されたファイル先頭の Markdown をその場で確認できます
4. ボタン下に表示されている URL は外部ブラウザでも開けます

#### 前提

- Node.js + npm が PATH に通っていること(`PreviewCommand="npm"` を上書きすれば他ツールでも可)
- worktree に `node_modules` が無い場合、`PreviewServerHost` が `PreviewCommand` + `PreviewInstallArguments` (既定: `npm install`) を自動実行してから preview server を起動します
- WebView2 の URL allow-list は `https` のみ通すデフォルトに加え、`PreviewSession` がプレビュー中の `http://localhost:{port}` だけを動的に許可します

#### node_modules の扱い (案 A / 案 B)

worktree ごとに作業ディレクトリが分かれるため、`npm install` の置き場所を選ぶ必要があります。既定ではアプリが各 worktree で自動実行します。ディスク消費を抑えたい場合は案 B を使ってください。

- **案 A — 各 worktree で個別に `npm install`** (デフォルト・確実):
   1. アプリが `node_modules` 不在を検知したら自動で `npm install` を実行
   2. 初回 5〜15 分・1〜2 GB 消費。worktree を削除すれば一緒に消えます
- **案 B — bare clone 親で 1 度だけ `npm install` → 各 worktree から junction で共有** (高速・ディスク節約):
   1. `cd <BareCloneDir> && npm install` を 1 回だけ実行
   2. 各 worktree で `cmd /c mklink /J node_modules "<BareCloneDir>\node_modules"` を作成
   3. `next dev` の watch は junction を透過するため、PR head を切り替えるたびに `node_modules` を再生成しなくて済みます
   - 注意: `package.json` の依存が PR 内で書き換わっている場合は案 A に戻るか、当該 worktree だけ junction を消して個別 install してください

#### キャッシュ (worktree) のクリーンアップ

放置すると `<WorktreeRoot>` 配下に PR head ごとの作業ディレクトリが溜まり続けます (1 件あたり 1〜2 GB)。次のいずれかで一括削除できます:

- **アプリ内**: プレビューパネルの **「キャッシュをクリーンアップ」** ボタン
   - 起動時に `git worktree list --porcelain` で既存 worktree を再ハイドレートしているため、前回プロセスで作られたものも含めて削除されます
- **CLI**: `pwsh ./scripts/Clean-Worktrees.ps1` (確認だけしたい場合は `-WhatIf`)
   - 既定で `appsettings.local.json → appsettings.json` の順に `DocsRepository:BareCloneDir` を読みます。明示指定したい場合は `-BareCloneDir <path>`

#### 仕組み

- [`DocsWorktreeManager`](../src/RepoSyncRadar.Core/Services/Preview/DocsWorktreeManager.cs) が `git clone --bare` した親リポから worktree を切り、[`PreviewServerHost`](../src/RepoSyncRadar.Core/Services/Preview/PreviewServerHost.cs) が `npm run dev` を sidecar 起動
- [`PreviewCoordinator`](../src/RepoSyncRadar.Core/Services/Preview/PreviewCoordinator.cs) が clone → fetch → checkout → start → `PreviewSession.Activate(port)` を 1 ステップで束ねます
- [`PreviewPathMapper`](../src/RepoSyncRadar.Core/Services/Preview/PreviewPathMapper.cs) が `content/foo/bar.md` を `/en/foo/bar` に、`content/index.md` を `/en` に変換します
- LRU で `MaxWorktrees` を超えたら最も古い worktree を `git worktree remove --force`
- アプリ終了時に preview プロセスは確実に kill されます(`IAsyncDisposable`)

### 5.2 監査ログ

すべての `radar_*` ツール呼び出しは `Audits` テーブルに記録(Step 12 の `OnPreToolUse` / `OnPostToolUse`)。Ask Palette で次のように確認できます:

```
過去 1 時間に呼ばれた radar_* ツールを多い順に
```

---

## 6. 既知の制約と運用 Tips

| 項目 | 状態 |
|---|---|
| 自動投稿 | **しない方針**(下書きまで。最終確認は人間) |
| 多言語 | 日本語のみ。英訳は媒体下書きの中で副次的に出るのみ |
| クラウド同期 | なし。`radar.db` を持ち運ぶ運用 |
| Velopack 自己更新 | **Step 20 で実装予定**。現状は `git pull && dotnet build` |
| GitHub 認証 | OAuth Device Flow で 1 度サインイン → DPAPI 暗号化トークンを `%LocalAppData%\RepoSyncRadar\github-token.bin` に保管。Copilot SDK と Octokit (`github/docs` 読み取り) で同じトークンを共有。 |

---

## 7. ビルド・テストのワンライナー

```powershell
# 厳格ビルド(警告=エラー)
dotnet build -warnaserror

# 自動テスト(手動カテゴリ除く / 全 167 件)
dotnet test --no-build --filter "Category!=Manual"

# 一発スクリプト
.\scripts\check.ps1
```

---

## 8. さらに深く知るには

- 設計の出発点と意思決定ログ → [DESIGN.md](DESIGN.md)
- ステップ別の実装範囲・テスト件数 → [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)
- Razor コンポーネントの構造 → [../src/RepoSyncRadar.App/Components/](../src/RepoSyncRadar.App/Components/)
- Copilot セッション層 → [../src/RepoSyncRadar.App/Copilot/](../src/RepoSyncRadar.App/Copilot/)
