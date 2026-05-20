---
name: preview-performance-investigation
description: 'Investigate and fix RepoSyncRadar preview performance regressions. USE FOR: プレビュー生成が遅い、docs previewが数十秒/数分かかる、WebView2表示が詰まる、cache cleanupが長い、worktree/fetch/Liquid/AUTOTITLE/Markdown renderingのボトルネック調査、CDPで実アプリを測定しながら局所修正と検証を行う。'
argument-hint: '(任意) 対象コミットSHA、PRタイトル、分類キュー、遅い操作、目標時間、report-only など'
---

# Preview Performance Investigation

## 役割

RepoSyncRadar の docs preview / Markdown comparison / cache cleanup の遅延を、実アプリで再現・測定し、根本原因を局所化して修正する。推測だけで終えず、progress text、WebView2/CDP、git操作単体の時間、テストを組み合わせて、ユーザーが感じている待ち時間が本当に短くなったことを確認する。

## いつ使うか

- 「このコミットのプレビューが遅い」「180秒かかった」など、特定の commit / PR / file で preview が重いとき
- cache cleanup、worktree cleanup、Next/dev server cleanup が長く UI を止めるとき
- Markdown preview に未展開の Liquid、AUTOTITLE、reusables、data table が残るとき
- `github/docs` の docs preview で fetch、checkout、worktree、Liquid rendering、WebView2 navigation のどこが詰まっているか切り分けたいとき
- 性能修正を実アプリ操作と full build/test gate まで通して確認したいとき

## 絶対ルール

1. **まず実アプリで再現する**。対象SHA、キュー、PRタイトル、file path、操作手順、progress/status text、経過時間を記録する。
2. **測定点を増やしてから直す**。長い処理を大まかに疑うだけでなく、fetch / parent lookup / git show / git ls-tree / Liquid context / render / server start / cleanup のどこで待っているか分ける。
3. **ファイルスケールのpreviewをrepoスケール処理にしない**。1ファイルMarkdown比較に full worktree checkout、全content scan、全reusables scan、全data scanを使っていないか最優先で疑う。
4. **正しさを落として速くしない**。Liquid variables、reusables、AUTOTITLE、data sequence for-loop、theme/readability、diff highlight が壊れていないことをテストで守る。
5. **UIを長時間ブロックしない**。巨大worktree削除や prune は、可能なら detach/rename して前面待ちを短くし、物理削除を背景に回す。
6. **既存変更を壊さない**。dirty worktree を前提に、関係ない変更は戻さない。
7. **検証は実測で閉じる**。最後に対象commitをもう一度実アプリで測り、短縮後の時間を報告する。

## 手順

### 1. 対象と期待値を固定する

ユーザーの説明から、次を抽出する。

- commit SHA、PR番号、PRタイトル、分類キュー
- 遅い操作: preview open / Markdown comparison / Next preview / cache cleanup / first navigation
- 遅い対象file path
- 体感時間または実測時間
- 許容目標: 数秒、十数秒、UIをブロックしない、など

不足している場合も、アプリDBやUI検索で分かるなら質問せずに調べる。

### 2. 事前状態を確認する

1. `git status --short` で既存変更を把握する。
2. `.github/copilot-instructions.md` と repo memory の preview 関連メモを確認する。
3. 既存の preview tests と対象コードを薄く確認する。

主な入口:

- `src/RepoSyncRadar.App/Components/CommitDetail.razor`
- `src/RepoSyncRadar.Core/Services/Preview/PreviewCoordinator.cs`
- `src/RepoSyncRadar.Core/Services/Preview/DocsWorktreeManager.cs`
- `src/RepoSyncRadar.Core/Services/Preview/DocsLiquidContextLoader.cs`
- `src/RepoSyncRadar.Core/Services/Preview/MarkdownPreviewRenderer.cs`
- `tests/RepoSyncRadar.Integrations.Tests/Preview/PreviewCoordinatorTests.cs`
- `tests/RepoSyncRadar.Integrations.Tests/Preview/DocsWorktreeManagerTests.cs`
- `tests/RepoSyncRadar.Core.Tests/Services/Preview/DocsLiquidContextLoaderTests.cs`

### 3. 実アプリで再現してタイムラインを取る

CDPを使えるように起動する。

```powershell
Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force
$env:REPOSYNCRADAR_BLAZOR_CDP_PORT='9223'
$env:REPOSYNCRADAR_DOCS_CDP_PORT='9224'
dotnet run --project src/RepoSyncRadar.App
```

`http://127.0.0.1:9223/json/list` で BlazorWebView target を確認する。対象キューを開き、SHAやタイトルで対象commitを選び、preview buttonを押す。測定では body全体の文字列検索より、次の `data-testid` を優先する。

- `commit-detail-open-in-webview`
- `commit-detail-preview-progress-text`
- `commit-detail-preview-status`
- `commit-detail-preview-cleanup-button`

タイムラインには、経過ms、progress text、status text、button disabled/text を記録する。どのprogressで何秒止まったかを先に確定する。

### 4. ボトルネックを枝分かれで切り分ける

観測したprogressに応じて、次の順で調べる。

#### fetch / commit availability が遅い

- `git cat-file -e <sha>^{commit}` が成功するなら fetch をskipできるか確認する。
- parent lookupだけが必要なら full fetch / checkout を避けられないか見る。
- remote fetch は必要な時だけ、timeoutとprogress表示を維持する。

#### Markdown比較なのに worktree checkout が遅い

- 1ファイルのMarkdown比較なら full worktree を作らない。
- bare clone に対する `git show <sha>:<path>` で before/after Markdown を読む。
- file listing は `git ls-tree` で必要ディレクトリだけ見る。
- full worktree は npm/Next preview など本当に必要なときだけ使う。

#### Liquid context loading が遅い

- Markdown本文と参照reusableから使われる `variables` roots だけ読む。
- 参照された `reusables` だけを再帰的に読む。
- `for entry in tables.x.y` のような data sequence だけ読む。
- AUTOTITLE は直接候補を先に読む。見つからない場合の `redirect_from` fallback は route近傍のdirectoryから狭い順に調べ、解決したら即終了する。
- `content/**/*.md`、`data/reusables/**/*.md`、`data/**/*.yml` の全scanに落ちていないか確認する。

#### Rendering後の表示が遅い

- `LocalPreviewContentServer`、WebView2 `Source` 更新、同一URI no-op、query string、route normalization を確認する。
- Markdown preview URLには content-affecting dimensions を含める。
- HTML生成後にWebView2 navigationが詰まっている場合は docs WebView CDP target も見る。

#### cache cleanup が遅い

- UI thread/foregroundで大きなdirectory削除をしていないか確認する。
- worktree metadata detach、directory rename to delete-pending、background physical delete、`git worktree prune` の順で前面待ちを短縮する。
- locked / initializing / missing path / untracked stale directory を復旧可能に扱う。

### 5. 単体計測で仮説を潰す

実アプリのprogressで怪しい箇所が見えたら、同じSHAとpathに対して小さな計測をする。

```powershell
$sha = '<target-sha>'
$parent = '<parent-sha>'
$repo = 'C:\github\.cache\docs.git'
Measure-Command { git --git-dir $repo cat-file -e "$sha^{commit}" }
Measure-Command { git --git-dir $repo show "$sha`:content/path/file.md" | Out-Null }
Measure-Command { git --git-dir $repo ls-tree -r --name-only $sha -- content/actions/reference | Out-Null }
```

git操作が数十msなのにアプリprogressが数十秒止まる場合は、アプリ側の反復、全scan、process resolution、sequential awaits、cache miss、UI waitを疑う。

### 6. 局所修正を実装する

修正は原因に対応する最小範囲にする。

よくある修正:

- local commitがある場合の fetch skip
- Markdown comparison の no-worktree path
- bare clone file source の導入
- Liquid context cache key を file path / commit SHA 単位にする
- referenced-only variables / reusables / data sequences の読み込み
- AUTOTITLE redirect fallback の scoped scan と early return
- cleanup の detach + background delete
- process runner の executable path resolution cache

実装後、関連テストを追加する。今回のような実例は、実SHAに依存しない形でモデル化する。

例:

- old route alias が隣接サブディレクトリの `redirect_from` にある
- Markdown previewでは worktree add が呼ばれない
- cleanupはforegroundで物理削除完了を待たない
- data sequence for-loop が展開され、未参照dataは読まない

### 7. focused validation を先に走らせる

変更範囲に応じて、狭いテストから実行する。

```powershell
dotnet test tests/RepoSyncRadar.Core.Tests/RepoSyncRadar.Core.Tests.csproj -- --filter-class RepoSyncRadar.Core.Tests.Services.Preview.DocsLiquidContextLoaderTests
dotnet test tests/RepoSyncRadar.Core.Tests/RepoSyncRadar.Core.Tests.csproj -- --filter-class RepoSyncRadar.Core.Tests.Services.Preview.DocsLiquidEvaluatorTests --filter-class RepoSyncRadar.Core.Tests.Services.Preview.MarkdownPreviewRendererTests
dotnet test tests/RepoSyncRadar.Integrations.Tests/RepoSyncRadar.Integrations.Tests.csproj -- --filter-class RepoSyncRadar.Integrations.Tests.Preview.PreviewCoordinatorTests --filter-class RepoSyncRadar.Integrations.Tests.Preview.DocsWorktreeManagerTests
```

警告はエラーとして扱われる。CA/IDE診断が出たら、小さく直して再実行する。

### 8. 実アプリで再測定する

最新ビルドでアプリを起動し直し、同じ対象commit・同じ操作をもう一度測る。

```powershell
dotnet build src/RepoSyncRadar.App/RepoSyncRadar.App.csproj -warnaserror
Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force
$env:REPOSYNCRADAR_BLAZOR_CDP_PORT='9223'
$env:REPOSYNCRADAR_DOCS_CDP_PORT='9224'
dotnet run --project src/RepoSyncRadar.App --no-build
```

報告には before / after を入れる。

- 対象commit / PRタイトル / file path
- 修正前の実測またはユーザー報告時間
- 修正後の実測時間
- どのprogress segmentが消えたか
- cache cleanup のforeground待ち時間

### 9. full gate で閉じる

最後に標準検証を通す。

```powershell
dotnet build RepoSyncRadar.sln -warnaserror
dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual
```

アプリを起動したまま終えない。不要なCDP付きプロセスは停止する。

## 品質基準

完了条件:

- 対象commitで遅延を再現、またはユーザー報告と同じ操作を測定している
- ボトルネックがprogress/timing/単体計測で説明できる
- 修正はrepo既存のpreview lifecycleに沿っている
- Markdown/Liquid previewの表示正しさを落としていない
- cache cleanupはUI前面待ちと背景削除の責務が分かれている
- focused tests と full build/test gate が通っている
- 実アプリでafter時間を測って報告している

## 完了レポート

最後は日本語で短くまとめる。

- 対象commit、PRタイトル、file path
- 原因: どの処理がrepoスケールに膨らんでいたか
- 修正: 主要ファイルと挙動変更
- 実測: before / after、cache cleanup時間
- 検証: focused tests、full build/test、実アプリ測定
- 残リスク: 外部サービス、初回fetch、Next previewなど今回の範囲外

## 参考ファイル

- Preview coordinator: [`src/RepoSyncRadar.Core/Services/Preview/PreviewCoordinator.cs`](../../../src/RepoSyncRadar.Core/Services/Preview/PreviewCoordinator.cs)
- Worktree manager: [`src/RepoSyncRadar.Core/Services/Preview/DocsWorktreeManager.cs`](../../../src/RepoSyncRadar.Core/Services/Preview/DocsWorktreeManager.cs)
- Liquid context loader: [`src/RepoSyncRadar.Core/Services/Preview/DocsLiquidContextLoader.cs`](../../../src/RepoSyncRadar.Core/Services/Preview/DocsLiquidContextLoader.cs)
- Markdown renderer: [`src/RepoSyncRadar.Core/Services/Preview/MarkdownPreviewRenderer.cs`](../../../src/RepoSyncRadar.Core/Services/Preview/MarkdownPreviewRenderer.cs)
- Commit detail UI: [`src/RepoSyncRadar.App/Components/CommitDetail.razor`](../../../src/RepoSyncRadar.App/Components/CommitDetail.razor)
- Preview integration tests: [`tests/RepoSyncRadar.Integrations.Tests/Preview/`](../../../tests/RepoSyncRadar.Integrations.Tests/Preview/)
- Preview core tests: [`tests/RepoSyncRadar.Core.Tests/Services/Preview/`](../../../tests/RepoSyncRadar.Core.Tests/Services/Preview/)