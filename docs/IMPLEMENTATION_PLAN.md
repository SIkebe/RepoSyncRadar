# RepoSyncRadar — 実装プラン

> 本書は [DESIGN.md](DESIGN.md) を実装に落とし込むための **段階的タスクリスト** です。各ステップは "自動テストが緑で通る" ことをもって完了とします。テストを書けないステップは **そのままでは未完了** と見なし、設計を見直すかテストを追加してから次へ進みます。
>
> 既に完了しているのは DESIGN.md §16 で言う **Phase 0(スキャフォールド)** までです。本書のステップ番号は Phase とは独立して、より粒度を細かくしています。

---

## 目次

- [0. 進め方とテスト戦略](#0-進め方とテスト戦略)
- [Step 1. テスト基盤を立てる](#step-1-テスト基盤を立てる)
- [Step 2. オプション / 設定バインディングを固める](#step-2-オプション--設定バインディングを固める)
- [Step 3. SQLite + EF Core スキーマを確定する](#step-3-sqlite--ef-core-スキーマを確定する)
- [Step 4. Frontmatter パーサと `PathToUrlResolver`](#step-4-frontmatter-パーサと-pathtourlresolver)
- [Step 5. `DocsApiClient`(docs.github.com 連携)](#step-5-docsapiclientdocsgithubcom-連携)
- [Step 6. `DocsGitHubClient`(Octokit 連携)](#step-6-docsgithubclientoctokit-連携)
- [Step 7. コミット取り込みパイプライン(冪等取り込み)](#step-7-コミット取り込みパイプライン冪等取り込み)
- [Step 8. プロンプトインジェクション対策とサニタイザ](#step-8-プロンプトインジェクション対策とサニタイザ)
- [Step 9. Razor コンポーネント(コミット一覧 + URL ペイン)](#step-9-razor-コンポーネントコミット一覧--url-ペイン)
- [Step 10. WebView2 で公式ページを埋め込む](#step-10-webview2-で公式ページを埋め込む)
- [Step 11. Copilot SDK の薄いラッパと権限ハンドラ](#step-11-copilot-sdk-の薄いラッパと権限ハンドラ)
- [Step 12. 監査フック(`OnPreToolUse` / `OnPostToolUse`)](#step-12-監査フックonpretooluse--onposttooluse)
- [Step 13. 読み取り系 `radar_*` ツール](#step-13-読み取り系-radar_-ツール)
- [Step 14. 書き込み系 `radar_*` ツール + 権限ダイアログ](#step-14-書き込み系-radar_-ツール--権限ダイアログ)
- [Step 15. Morning Triage セッション](#step-15-morning-triage-セッション)
- [Step 16. Review UI(注目 / 保留 / 見送り候補 / アーカイブ / Ignore)](#step-16-review-ui注目--保留--見送り候補--アーカイブ--ignore)
- [Step 17. Adoption セッション + 共有文案](#step-17-adoption-セッション--共有文案)
- [Step 18. `radar_query` と Ask Palette](#step-18-radar_query-と-ask-palette)
- [Step 19. ローカルプレビュー(bare clone + worktree)](#step-19-ローカルプレビューbare-clone--worktree)
- [Step 19.5. ローカルプレビューの UI 配線](#step-195-ローカルプレビューの-ui-配線)
- [Step 19.6. ローカルプレビューの起動時間を抜本短縮](#step-196-ローカルプレビューの起動時間を抜本短縮)
- [Step 19.7. 対象ページのインプロセス・レンダリング (Next.js バイパス)](#step-197-対象ページのインプロセスレンダリング-nextjs-バイパス)
- [Step 19.8. 公式 docs へのジャンプ + Liquid タグの可視化](#step-198-公式-docs-へのジャンプ--liquid-タグの可視化)
- [Step 19.9. ifversion / バージョン認識 + Version ComboBox](#step-199-ifversion--バージョン認識--version-combobox)
- [Step 20. 配布(Velopack)とシークレット保管(DPAPI)](#step-20-配布velopackとシークレット保管dpapi)
- [付録 A — テスト命名規約](#付録-a--テスト命名規約)
- [付録 B — テスト固定値 / フィクスチャ管理](#付録-b--テスト固定値--フィクスチャ管理)
- [付録 C — E2E テスト (Playwright over WebView2 CDP)](#付録-c--e2e-テスト-playwright-over-webview2-cdp)

---

## 0. 進め方とテスト戦略

### 0.1 ルール

1. **テストファースト寄り**: 各ステップは「失敗するテスト → 実装 → テストが緑」の順で進める。完全な TDD は強制しないが、コミット時には対応するテストが同梱されていなければレビューを通さない。
2. **CI を回せる構成**: テストはすべて `dotnet test` 一発で通る。WPF / BlazorWebView の起動を要するテストは作らない(必要なら手動確認チェックリストにする)。
3. **ネットワーク非依存**: 既定では `docs.github.com` / `api.github.com` を直接叩かない。HTTP は `RichardSzalay.MockHttp`、Octokit は `IGitHubClient` のスタブで差し替える。録画済みの JSON は `tests/Fixtures/` に置く。
4. **Copilot SDK は抽象越し**: 実装の中核は `ICopilotAgent` / `ICopilotSessionFactory` 越しに置く。SDK の `CopilotClient` をテストで起動しない。SDK 統合の確認は別途「手動スモークテスト」で行う(Step 11 末尾)。
5. **可逆な変更のみ自動化**: スキーマ migrate / `git fetch` / `dotnet build` は OK。`git push` / リリースは手動。

### 0.2 テストスタック

| 領域 | 採用 | 備考 |
|---|---|---|
| テスト基盤 | **xUnit v3** + `Microsoft.Testing.Platform` | .NET 10 と相性が良い |
| アサーション | xUnit 標準 `Assert` | FluentAssertions はライセンス問題があるので導入しない |
| モック | **NSubstitute** | `IGitHubClient` / `IDocsApiClient` のスタブ |
| HTTP モック | **RichardSzalay.MockHttp** | `HttpClient` 差し込み |
| Razor コンポーネントテスト | **bUnit** | Workbench 等の Razor のレンダリング検証 |
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` を `Data Source=:memory:` で利用 | 物理ファイル不要 |
| カバレッジ | `Microsoft.CodeCoverage`(`dotnet test --coverage`) | 80% 以上を目標 |

### 0.3 リポジトリへの追加物(本書範囲)

- `tests/RepoSyncRadar.Core.Tests/` — ドメイン / リゾルバ / リポジトリ層
- `tests/RepoSyncRadar.Integrations.Tests/` — `DocsApiClient` / `DocsGitHubClient` の HTTP / Octokit テスト
- `tests/RepoSyncRadar.App.Tests/` — bUnit / 権限ハンドラ / セッションオーケストレータ
- `tests/Fixtures/` — JSON 録画、生 diff サンプル、`llms.txt` 抜粋など

### 0.4 完了の定義(各ステップ共通)

ステップ X の完了条件は、以下が **すべて緑** で揃うこと。

```powershell
dotnet build -warnaserror
dotnet test --no-build -- --filter-not-trait Category=Manual
```

加えて、各ステップ末尾の **「完了基準」** に列挙されたテストクラス / シナリオが実在し、通過していること。

---

## Step 1. テスト基盤を立てる

### 1.1 目的

xUnit v3 ベースのテストプロジェクトをソリューションに足し、最初のスモークテストを通す。以降の全ステップが寄りかかる土台。

### 1.2 スコープ

- `Directory.Packages.props` に以下を追加
  - `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.Testing.Platform`
  - `NSubstitute`, `RichardSzalay.MockHttp`, `bunit`
  - `Microsoft.EntityFrameworkCore.InMemory`(参考用、本番では Sqlite in-memory を使う)
- `Directory.Build.props` で `TreatWarningsAsErrors=true` のままにする(テストプロジェクトも同じ厳格度で書く)
- 3 つのテスト csproj をスケルトンで作成
  - [tests/RepoSyncRadar.Core.Tests/RepoSyncRadar.Core.Tests.csproj](tests/RepoSyncRadar.Core.Tests/RepoSyncRadar.Core.Tests.csproj)
  - [tests/RepoSyncRadar.Integrations.Tests/RepoSyncRadar.Integrations.Tests.csproj](tests/RepoSyncRadar.Integrations.Tests/RepoSyncRadar.Integrations.Tests.csproj)
  - [tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj](tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj)
- ソリューションへ追加し、Solution Folder `tests` 配下に並べる。

### 1.3 テスト

- `Core.Tests/SmokeTests.cs` — `Assert.True(true)` の自明テスト 1 件
- `Integrations.Tests/SmokeTests.cs` — 同上
- `App.Tests/SmokeTests.cs` — 同上(`bunit` の TestContext が new できる確認も含める)

### 1.4 完了基準

- `dotnet sln list` に 3 つのテストプロジェクトが現れる
- `dotnet test` が 3 アセンブリでパスする
- CI が無い段階でも、PowerShell スクリプト `scripts/check.ps1`(`dotnet build -warnaserror; dotnet test`)を 1 本置く

---

## Step 2. オプション / 設定バインディングを固める

### 2.1 目的

`GitHubOptions` / `DocsApiOptions` / `CopilotOptions` の妥当性を **アプリ起動前** に検証し、間違った `appsettings.json` でサイレントに動き続けない状態を作る。

### 2.2 スコープ

- 各 `*Options` に `DataAnnotations` 属性(`Required` / `Url` / `Range`)を付与
- `services.AddOptions<X>().ValidateDataAnnotations().ValidateOnStart()` を [`CoreServiceCollectionExtensions`](../src/RepoSyncRadar.Core/CoreServiceCollectionExtensions.cs) に追加
- `AllowedUrlHosts` の小文字化 / 重複排除を `IValidateOptions<CopilotOptions>` で実施

### 2.3 テスト

`Core.Tests/Options/OptionsValidationTests.cs`

| ケース | 期待 |
|---|---|
| 正常な appsettings JSON 文字列をバインド | `ValidateOnStart` 相当の検証が通る |
| `GitHub:Owner` 空文字 | `OptionsValidationException` |
| `DocsApi:BaseAddress` が `http://` | `OptionsValidationException`(HTTPS のみ許可) |
| `Copilot:AllowedUrlHosts` に重複 / 大文字 | バインド後は小文字でユニーク |
| `Copilot:DefaultModel` 空 | バリデーション失敗 |

### 2.4 完了基準

- 上記 5 シナリオが緑
- `dotnet run --project src/RepoSyncRadar.App` で起動した際、不正な `appsettings.Local.json` を渡すと起動時に例外で落ちることを `App.Tests` の `HostStartupValidationTests` で検証(`Host.CreateApplicationBuilder` を直接組んで `host.Start()` まで実行する形に切り出す)

---

## Step 3. SQLite + EF Core スキーマを確定する

### 3.1 目的

DESIGN.md §10 のドメインモデルを EF Core の **初回 migration** として固める。以降は migration 追加でのみスキーマを変える。

### 3.2 スコープ

- `dotnet ef migrations add InitialCreate -p src/RepoSyncRadar.Core -s src/RepoSyncRadar.App` で migration 生成
- アプリ起動時に `db.Database.Migrate()` を呼ぶブートストラップを `RepoSyncRadar.App` 側に追加
- インデックス確認: `Commits(PrNumber, AuthoredAt)`, `Scoring(Score)`, `Review(Status)`, `Draft(Sha, Channel)`, `CopilotToolLog(SessionId, ToolName)`

### 3.3 テスト

`Core.Tests/Data/RadarDbContextTests.cs`(SQLite in-memory: `new SqliteConnection("Data Source=:memory:")` を共有して `EnsureCreated`)

| テスト | 内容 |
|---|---|
| `Migrate_Creates_All_Tables` | `db.Database.Migrate()` 後に期待した DbSet がクエリ可能 |
| `Commit_Cascade_Deletes_Children` | `Commit` を削除すると `CommitFile` / `Scoring` / `Review` / `Draft` も消える |
| `Review_Status_Roundtrip` | `ReviewStatus.Adopted` を `string` として保存して読み戻し |
| `PathUrlMap_Composite_Key_Unique` | 同じ (Path, Version, Language) で重複インサート → 例外 |
| `IgnoreRule_Pattern_Unique` | 同じパターンを 2 回 Add → 例外 |
| `CopilotToolLog_Auto_Id` | `Id` が自動採番される |

### 3.4 完了基準

- `tests/RepoSyncRadar.Core.Tests/Data/` の上記 6 件が緑
- `Migrations/` フォルダがリポジトリにコミット済み
- 既存 DB を持つ環境(`%LOCALAPPDATA%\RepoSyncRadar\radar.db`)を **壊さない**(初回 migration なので何もしない)

---

## Step 4. Frontmatter パーサと `PathToUrlResolver`

### 4.1 目的

`content/<area>/.../<page>.md` のリポジトリ相対パスと frontmatter の `versions:` ブロックから、`docs.github.com` の正規 URL を **オフラインで** 解決できるようにする。pagelist API は別ステップ。

### 4.2 スコープ

- `RepoSyncRadar.Core/Services/Frontmatter/FrontmatterParser.cs`(YAML フロントマター抽出 + `versions:` 部分のみ気にする最小実装)
- `RepoSyncRadar.Core/Services/PathToUrlResolver.cs`
  - `ResolveAsync(string repoPath, string frontmatterVersions, IReadOnlyDictionary<string, IReadOnlyList<string>> pageListByVersion)`
  - pagelist は引数で受け取る(キャッシュ層は Step 5)
- versions 表記の解釈: `fpt`, `ghec`, `ghes >= 3.14`, `ghes < 3.15` 等を内部の `VersionId` 列挙へ正規化

### 4.3 テスト

`Core.Tests/Services/PathToUrlResolverTests.cs`

| ケース | 入力 | 期待 |
|---|---|---|
| 単一バージョン | `content/copilot/about-copilot.md` / `versions: { fpt: '*' }` | `/en/copilot/about-copilot` 1 件 |
| 複数バージョン | `ghes ≤ 3.15` を含む | `ghes-3.13` / `ghes-3.14` / `ghes-3.15` の 3 URL |
| pagelist にヒットしない | path が pagelist に無い | 空配列(`ResolveCanonicalAsync` フォールバックは別途、Step 5) |
| `data/` 配下(`release-notes`) | `data/release-notes/...` | 解決対象外として空配列 |
| 言語が `ja` の pagelist が無い | デフォルト言語 `en` にフォールバック | en の URL |
| frontmatter が空 / 不正 | `FormatException` ではなく `IReadOnlyList<string>.Empty` |

`Core.Tests/Services/FrontmatterParserTests.cs`

| ケース | 期待 |
|---|---|
| 標準的な `---\nversions:\n  fpt: '*'\n---` | versions 抜き出し |
| frontmatter 無し | `null` 返却 |
| `---` が閉じていない | `FormatException` |

### 4.4 完了基準

- 上記 9 シナリオが緑
- `PathToUrlResolver` は **HTTP も DB もアクセスしない**(純粋関数として扱える)

---

## Step 5. `DocsApiClient`(docs.github.com 連携)

### 5.1 目的

`docs.github.com/api/*` を叩く実装を `RepoSyncRadar.Core/Services/Docs/DocsApiClient.cs` として確定する。pagelist のディスクキャッシュ層を併設する。

### 5.2 スコープ

- `IDocsApiClient` の実装(`HttpClient` を DI、`BaseAddress` は `DocsApiOptions` から)
- `User-Agent` に `ClientName/version` を必ず付ける
- `GetPageListAsync(language, version)` の結果を `PageListCacheSeconds` で SQLite (`PathUrlMap` とは別の `PageListCache` テーブル) または `IMemoryCache` にキャッシュ
- `GetArticleBodyAsync(pathname)` は HTML 文字列をそのまま返す
- `ResolveCanonicalAsync(pathname)` は `/api/article/meta` のレスポンスから `redirectedFrom` / `canonical` を参照
- 例外: 404 → `DocsArticleNotFoundException`, 4xx/5xx → `DocsApiException`(本文を含める)

### 5.3 テスト

`Integrations.Tests/Docs/DocsApiClientTests.cs`(`RichardSzalay.MockHttp` で `BaseAddress` 配下を模す)

| テスト | 内容 |
|---|---|
| `GetPageListAsync_Returns_Parsed_Paths` | `/api/pagelist/en/fpt` の固定 JSON を返し、配列を取得 |
| `GetPageListAsync_Uses_Cache_On_Second_Call` | 同一 (lang, version) で 2 回呼び、HTTP は 1 回だけ起きる |
| `GetPageListAsync_Refreshes_After_Ttl` | `Clock` 抽象を進めて TTL 超過 → HTTP が再度呼ばれる |
| `GetArticleBodyAsync_Returns_Body_Html` | レスポンスをそのまま返す |
| `ResolveCanonicalAsync_Returns_RedirectedFrom_Target` | redirect ありのレスポンスで canonical を取得 |
| `ResolveCanonicalAsync_NotFound_Returns_Null` | 404 で `null` を返す |
| `Non2xx_Throws_DocsApiException` | 500 で例外 |
| `UserAgent_Header_Sent` | リクエストヘッダに `reposyncradar/...` |

### 5.4 完了基準

- 上記 8 件が緑
- `DocsApiClient` は **`HttpClient` を直接 new しない**(`AddHttpClient<IDocsApiClient, DocsApiClient>()` 経由)
- `App` 側の DI 拡張に `AddHttpClient<IDocsApiClient, DocsApiClient>()` を追加

---

## Step 6. `DocsGitHubClient`(Octokit 連携)

### 6.1 目的

`IDocsGitHubClient` を Octokit で実装し、Repo sync PR の最新コミット一覧 / 個別の files / unified diff を取れるようにする。

### 6.2 スコープ

- `RepoSyncRadar.Core/Services/GitHub/DocsGitHubClient.cs`
- Octokit の `IGitHubClient` を DI(`RepoSyncRadar.App` で `new GitHubClient(...)` を生成)
- `FetchUnseenCommitsAsync` の挙動
  - 直近更新 PR をページングし、タイトルが `PullRequestTitleFilter` で始まる PR を `MaxPullRequests` 件集める
  - `PullRequestCreatedAtOrAfter` が指定されている場合は、PR 作成日降順でページングし、その日時以降に作成された PR だけを候補にする
  - 各 PR の commits をページネーション込みで列挙
  - DB に既存の `Sha` を除外(`IRadarRepository` 経由)
  - 1 コミットあたりの files は **遅延ロード**(`GetCommitFilesAsync(sha)` を別メソッドで提供)
- `GetUnifiedDiffAsync(sha)` は `application/vnd.github.v3.diff` を `Accept` ヘッダにセットして raw を取得
- 認証
  - `GitHubOptions.PersonalAccessToken` が空なら DPAPI から(Step 20 で実装、ここでは未指定なら anonymous 動作 + ハードな rate limit 警告ログ)

### 6.3 テスト

`Integrations.Tests/GitHub/DocsGitHubClientTests.cs`(`NSubstitute` で `IGitHubClient` をモック)

| テスト | 内容 |
|---|---|
| `FetchUnseenCommitsAsync_Filters_By_Title` | 5 件中タイトルが `Repo sync` で始まる 3 件のみ採用 |
| `FetchUnseenCommitsAsync_Excludes_Known_Shas` | `IRadarRepository.GetKnownShasAsync` のスタブが返した SHA を除外 |
| `FetchUnseenCommitsAsync_Paginates_Until_Title_Matches_Reach_Limit` | `Repo sync` が必要件数集まるまで PR 一覧をページング |
| `GetUnifiedDiffAsync_Sets_Accept_Header` | Octokit の `IConnection.Get<string>` 呼び出し時の Accept ヘッダを検証 |
| `GetFileContentAsync_Decodes_Base64` | `RepositoryContent.EncodedContent` を base64 デコード |
| `Token_Empty_Logs_Warning` | `ILogger<DocsGitHubClient>` に warning が出る |

### 6.4 完了基準

- 上記 6 件が緑
- `IRadarRepository` という薄い IF を `RepoSyncRadar.Core/Data/IRadarRepository.cs` として導入(Step 7 で本体)

---

## Step 7. コミット取り込みパイプライン(冪等取り込み)

### 7.1 目的

`IDocsGitHubClient` から取ってきたコミット群を SQLite に **冪等に** 入れる。再実行で重複を作らない。`FetchedAt` を更新するか、初回挿入のみとするかは "初回挿入のみ" とする(再 fetch しない既知 SHA は触らない)。

### 7.2 スコープ

- `RepoSyncRadar.Core/Data/RadarRepository.cs`
  - `GetKnownShasAsync(IEnumerable<string> shas)`
  - `UpsertCommitsAsync(IEnumerable<Commit> commits)`
  - `SetReviewAsync(string sha, ReviewStatus, string? reason)`
- `RepoSyncRadar.Core/Services/CommitIngestionService.cs`
  - `IDocsGitHubClient` から取得 → 既知除外 → `IRadarRepository.UpsertCommitsAsync` → `IDocsGitHubClient.GetCommitFilesAsync` で files 注入

### 7.3 テスト

`Core.Tests/Data/RadarRepositoryTests.cs`(SQLite in-memory)

| テスト | 内容 |
|---|---|
| `UpsertCommitsAsync_Inserts_New` | 0 件 → 3 件挿入 |
| `UpsertCommitsAsync_Skips_Existing` | 既存 SHA は更新せず、`FetchedAt` も維持 |
| `UpsertCommitsAsync_Persists_Files` | `Commit.Files` がカスケード保存 |
| `GetKnownShasAsync_Returns_Intersection` | 3 件渡して既知 1 件 |
| `SetReviewAsync_Creates_When_Missing` | Review 行を新規作成 |
| `SetReviewAsync_Updates_When_Present` | 既存の Status を更新 |

`Core.Tests/Services/CommitIngestionServiceTests.cs`(`NSubstitute` で `IDocsGitHubClient` をスタブ)

| テスト | 内容 |
|---|---|
| `IngestAsync_Persists_Only_New` | known/new を区別して repository へ |
| `IngestAsync_Counts_Returned_Correctly` | 戻り値の `IngestionReport { Total, Inserted, Skipped }` |
| `IngestAsync_Respects_CancellationToken` | キャンセルすると `OperationCanceledException` |

### 7.4 完了基準

- 上記 9 件が緑
- 既存テストとの干渉(同一 DB ファイル) が無い(各テストで `Data Source=:memory:` の `SqliteConnection` を Open し続ける)

---

## Step 8. プロンプトインジェクション対策とサニタイザ

### 8.1 目的

Copilot に渡す前にコミット本文 / diff を **untrusted データ** として包む。URL / トークン / メアド / PII を正規表現でマスクする。

### 8.2 スコープ

- `RepoSyncRadar.Core/Services/Sanitization/UntrustedTextWrapper.cs`
  - `Wrap(string title, string content)` → `<<<UNTRUSTED:{title}>>>\n{content}\n<<<END>>>` のような明示マーカーで囲む
- `RepoSyncRadar.Core/Services/Sanitization/SecretMasker.cs`
  - GitHub PAT, JWT, OpenAI / Anthropic キー, メールアドレス, IPv4, 12 桁数字(電話番号風)
  - 既知パターン: `gh[pousr]_[A-Za-z0-9]{36,}`, `eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+`, `sk-[A-Za-z0-9]{20,}`

### 8.3 テスト

`Core.Tests/Sanitization/SecretMaskerTests.cs`

| ケース | 入力 | 期待 |
|---|---|---|
| GitHub PAT | `ghp_AAAA...36+` | `***GITHUB_PAT***` |
| OpenAI key | `sk-AAAA...` | `***OPENAI_KEY***` |
| JWT | `eyJ...` | `***JWT***` |
| Email | `foo@example.com` | `***EMAIL***` |
| IPv4 | `192.168.1.10` | `***IPV4***` |
| 既存マスクとの重複 | 同じ文字列に複数該当 | 後勝ち / 既マスク部分を再マスクしない |

`Core.Tests/Sanitization/UntrustedTextWrapperTests.cs`

| ケース | 期待 |
|---|---|
| 通常テキスト | マーカーで囲まれる |
| 入力に `<<<UNTRUSTED:` が含まれる | エスケープされる(置換 or 別マーカー) |

### 8.4 完了基準

- 上記 8 件が緑
- 後続ステップ(Copilot ツール)で **必ず** `UntrustedTextWrapper` を経由する

---

## Step 9. Razor コンポーネント(コミット一覧 + URL ペイン)

### 9.1 目的

Phase 1 の最低 UI:「サイドバーで未確認 / コミット一覧」「中央でコミット詳細(files + URL)」を出す。Diff の Monaco / WebView2 / レビュー判断ボタンは別ステップ。

### 9.2 スコープ

- `RepoSyncRadar.App/Components/CommitList.razor` — `IRadarRepository.QueryCommitsAsync(filter)` で取得して表示
- `RepoSyncRadar.App/Components/CommitDetail.razor` — files、frontmatter、resolved URL のリスト
- `RepoSyncRadar.App/Components/Sidebar.razor` — 未確認 / 注目 / 保留 / 見送り候補 / アーカイブのカウンタ(内部は Unseen / Adopted / Later / Rejected / Archived、Seen は未確認へ合算)
- 状態管理は最小限。`CascadingValue` で `IServiceProvider` 配って各コンポーネントが自分で必要なサービスを取る

### 9.3 テスト

`App.Tests/Components/`(bUnit)

| テスト | 内容 |
|---|---|
| `Sidebar_Shows_Counts_From_Repository` | リポジトリスタブが `{Unseen=3, Adopted=1}` を返すと「未確認」「注目」の UI に反映 |
| `CommitList_Renders_Rows` | 3 件で 3 行 |
| `CommitList_Empty_State` | 0 件で空メッセージ |
| `CommitDetail_Shows_Resolved_Urls` | `PathToUrlResolver` のスタブが返す URL がリンクとして並ぶ |
| `CommitDetail_Shows_File_Stats` | `+42 -5` などの additions/deletions |

`Core.Tests/Data/` 側で、UI から新規に呼ぶ Repository 拡張も in-memory SQLite で検証する:

| テスト | 内容 |
|---|---|
| `QueryCommitsAsync_Filters_By_Status_And_Limit` | `Status` / `Limit` フィルタと AuthoredAt desc の並び、`Unseen` が Review 行なしのコミットを拾うこと |
| `GetReviewCountsAsync_Counts_All_Buckets` | ユーザー向け 5 ステータスと legacy Seen を 0 埋めで返し、Review 行のないコミットが Unseen に積まれること |

### 9.4 完了基準

- bUnit テストが緑
- 手動確認チェックリスト: `dotnet run --project src/RepoSyncRadar.App` で起動し、テストデータをシードした DB で表示できることを目視確認(自動化対象外)

---

## Step 10. WebView2 で公式ページを埋め込む

### 10.1 目的

DESIGN.md §9.3 のレンダリングモード B / C を実装。

- B: BlazorWebView 内 iframe + `srcdoc` に `GetArticleBodyAsync` の HTML を流し込む
- C: 別ペインの `Microsoft.Web.WebView2.Wpf.WebView2` で `Source = canonical URL`

### 10.2 スコープ

- `RepoSyncRadar.App/Components/ArticleBodyPane.razor`(iframe srcdoc + Body API)
- `RepoSyncRadar.App/MainWindow.xaml` を 2 ペイン化、左 BlazorView / 右 WebView2
- 通信先を `Copilot:AllowedUrlHosts` で制限(WebView2 の `WebResourceRequested` で host を allow-list 照合、それ以外は cancel)
- WebView2 を使えない環境(EvergreenBootstrapper が無い等)を検知して fallback メッセージ

### 10.3 テスト

| テスト | 内容 |
|---|---|
| `Core.Tests/Services/UrlAllowListTests.cs` `IsAllowed_*` | `UrlAllowList(["docs.github.com"]).IsAllowed("https://docs.github.com/foo") == true` 等 6 件のパラメタライズ |
| `App.Tests/Components/ArticleBodyPaneTests.cs` `Renders_Iframe_With_Srcdoc` | bUnit で `iframe[srcdoc]` 属性が API レスポンスで埋まる |
| `App.Tests/Components/ArticleBodyPaneTests.cs` `Shows_Error_On_404` | `DocsArticleNotFoundException` 時にエラーメッセージ |

WebView2 本体の検証は **手動スモークテスト**:`/en/copilot/...` を URL バーに入れて表示できることを確認。

### 10.4 完了基準

- 自動テストが緑
- `UrlAllowList` がパスを問わずホスト単位で機能
- 手動: WebView2 ペインに `docs.github.com` の任意ページが表示でき、`example.com` がブロックされる

---

## Step 11. Copilot SDK の薄いラッパと権限ハンドラ

### 11.1 目的

Copilot SDK との接合点を「ICopilotAgent → ICopilotSessionFactory → CopilotClient」の 3 層に薄く分け、SDK を 1 か所だけ参照する。`OnPermissionRequest` を必ず実装。

### 11.2 スコープ

- `RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs` — `CopilotClient` 1 個を所有、`CreateSessionAsync(SessionPurpose)` で `SessionConfig` を組む
  - `SystemMessageMode = Append`
  - `Streaming = options.Streaming`
  - `Model = options.DefaultModel`
- `RepoSyncRadar.App/Copilot/RadarPermissionPolicy.cs` — `PermissionRequestHandler` を実装
  - `custom_tool` / `read` → Approve
  - `url` → `UrlAllowList.IsAllowed` で Approve / 否なら UI 確認(`IPermissionPrompt`)
  - `write` / `shell` → 必ず UI 確認
- `IPermissionPrompt` を抽象化し、本番は WPF MessageBox、テストは事前回答のスタブ
- 認証
  - 環境変数 `COPILOT_GITHUB_TOKEN` が立っていればデバッグ override として Copilot SDK / Octokit に渡す。`GH_TOKEN` / `GITHUB_TOKEN` は他ツール用 token の誤用を避けるため読まない
  - 立っていない場合、同梱または上書きされた `Copilot:OAuthClientId` で GitHub Device Flow を実行し、取得 token を DPAPI store に保存する

### 11.3 テスト

`App.Tests/Copilot/RadarPermissionPolicyTests.cs`

| ケース | 期待 |
|---|---|
| `custom_tool` request | Approve |
| `read` request | Approve |
| `url` allow-list ヒット | Approve |
| `url` allow-list ミス + ユーザー承認 | Approve(`IPermissionPrompt` が `true` を返した) |
| `url` allow-list ミス + ユーザー拒否 | `DeniedByUser` |
| `write` | UI 確認後 `DeniedByUser` / `Approve` の両ケース |
| `shell` 未知のコマンド | `DeniedByRules` |
| 未知の `Kind` | `DeniedByRules` |

`CopilotSessionFactory` 自体は SDK 実装に強く依存するので **インスタンス生成テスト 1 件**(`SessionConfig` のプロパティ確認)のみ。

### 11.4 完了基準

- 自動テスト 8+1 件が緑
- 手動スモーク(`Category=Manual` でマーク、`dotnet test -- --filter-trait Category=Manual` で別走):
  - 実際の `CopilotClient` を起動
  - `radar_list_commits` を 1 件だけ登録した最小セッションが `SessionIdleEvent` まで進む

---

## Step 12. 監査フック(`OnPreToolUse` / `OnPostToolUse`)

### 12.1 目的

すべてのツール呼び出しを `CopilotToolLog` に記録し、別途 JSONL にも追記する。デバッグとセキュリティ後追いの両用。

### 12.2 スコープ

- `RepoSyncRadar.App/Copilot/ToolAuditHook.cs`
  - `OnPreToolUse` で `(SessionId, ToolName, ArgsJson, StartedAt)` を新規行として INSERT、ID を返す
  - `OnPostToolUse` で その ID に対して `ResultJson, EndedAt` を UPDATE
  - 同時に JSONL (`%LOCALAPPDATA%\RepoSyncRadar\audit\YYYY-MM-DD.jsonl`) へ 1 行追記
- 例外時も `ResultJson = {"error": ...}` で必ず UPDATE する

### 12.3 テスト

`App.Tests/Copilot/ToolAuditHookTests.cs`

| テスト | 内容 |
|---|---|
| `PreUse_Inserts_Row_With_Started` | DB に行ができる |
| `PostUse_Completes_Row` | EndedAt が入る |
| `PostUse_On_Error_Records_Error_Json` | `error` フィールドあり |
| `Jsonl_Appends_Line` | テンポラリフォルダに 1 行追加される |
| `Concurrent_PreUse_Has_Unique_Ids` | 並列 10 件で衝突しない |

### 12.4 完了基準

- 上記 5 件が緑
- JSONL ファイルパスは `IFileSystem` 抽象越し(テストで `TempFileSystem` を差し替え)

---

## Step 13. 読み取り系 `radar_*` ツール

### 13.1 目的

副作用の無いツールを実装、`skip_permission = true` を付ける。

| ツール | 機能 |
|---|---|
| `radar_list_commits` | フィルタ付きでコミットを返す(JSON 配列) |
| `radar_get_diff` | SHA の unified diff(マスク済み) |
| `radar_resolve_url` | path + frontmatter から URL 配列 |
| `radar_fetch_rendered` | `pathname` のレンダリング済み HTML |

### 13.2 スコープ

- `RepoSyncRadar.App/Copilot/Tools/RadarTools.cs` 内で `AIFunctionFactory.Create` を使い、内部で `IRadarRepository` / `IDocsApiClient` / `PathToUrlResolver` を呼ぶ
- 戻り値は **JSON シリアライズ可能な POCO**(Source-generated `JsonSerializerContext` で型安全に)
- `radar_get_diff` は `UntrustedTextWrapper` + `SecretMasker` を必ず通す

### 13.3 テスト

`App.Tests/Copilot/Tools/RadarToolsTests.cs`

| テスト | 内容 |
|---|---|
| `RadarListCommits_Filters_By_Status` | スタブ repository が返したものを Status フィルタで絞る |
| `RadarListCommits_Honors_Limit` | 既定上限 50、明示指定可能 |
| `RadarGetDiff_Masks_Secrets` | `ghp_...` が `***GITHUB_PAT***` に置換されている |
| `RadarGetDiff_Wraps_Untrusted` | 戻り値に `<<<UNTRUSTED:` マーカー |
| `RadarResolveUrl_Returns_Resolver_Output` | スタブ resolver の戻りがそのまま返る |
| `RadarFetchRendered_Returns_Body_Html` | スタブ `IDocsApiClient` の戻り |
| `RadarFetchRendered_Throws_On_NotFound` | 404 で AIFunction が `error` をセットする(throw を吸って `error` JSON で返す方針) |

### 13.4 完了基準

- 上記 7 件が緑
- すべてのツール戻り値が `JsonSerializer.SerializeToUtf8Bytes` でラウンドトリップする(POCO テストを 1 件追加)

---

## Step 14. 書き込み系 `radar_*` ツール + 権限ダイアログ

### 14.1 目的

副作用ありのツールは **必ず** UI 承認を経由。

| ツール | 機能 |
|---|---|
| `radar_score_commit` | `Scoring` を INSERT/UPDATE |
| `radar_save_review` | `Review` を更新(注目 / 見送り候補 / アーカイブ / 保留。内部値は Adopted/Rejected/Archived/Later) |
| `radar_post_draft` | `Draft` を INSERT(Posted=false) |
| `radar_ignore_rule` | `IgnoreRule` を追加 |
| `radar_boost_rule` | `BoostRule` を追加 |

### 14.2 スコープ

- 各ツールは `[Description]` で「副作用あり」を明示し、`skip_permission = false`
- 引数は **POCO** 受け取り(`SaveReviewArgs { string Sha, ReviewStatus Status, string? Reason }`)
- バリデーション失敗時は AIFunction の戻りに `error` を入れる(throw しない)

### 14.3 テスト

`App.Tests/Copilot/Tools/WriteToolsTests.cs`

| テスト | 内容 |
|---|---|
| `SaveReview_Persists_To_Db` | in-memory SQLite に書き込み |
| `SaveReview_Updates_Existing` | 同じ SHA を 2 回 → 上書き |
| `SaveReview_Rejects_Unknown_Sha` | error が返る |
| `ScoreCommit_Stores_PromptHash_And_Model` | Scoring 行に Model / PromptHash |
| `PostDraft_Allows_Empty_Body_Optional` | デフォルト Body は空文字 |
| `IgnoreRule_Duplicate_Pattern_Returns_Error` | 既存パターンは error 返却 |
| `BoostRule_Out_Of_Range_Delta` | `±5.0` 範囲外でバリデーション error |

`App.Tests/Copilot/PermissionFlowTests.cs`

| テスト | 内容 |
|---|---|
| `WriteTool_Triggers_Permission_Prompt` | `IPermissionPrompt.AskAsync` が呼ばれる |
| `WriteTool_Denied_Returns_DeniedByUser` | プロンプトが false を返すと拒否 |

### 14.4 完了基準

- 上記 9 件が緑
- 既存の `ToolAuditHook` テスト(Step 12)が新ツールでも通る

---

## Step 15. Morning Triage セッション

### 15.1 目的

「最新の Repo sync PR を取り込み、未確認を全件スコアリング → サイドバーに反映」までを 1 メソッド `RunMorningTriageAsync` で完結させる。

### 15.2 スコープ

- `RepoSyncRadar.App/Copilot/MorningTriageSession.cs` が `ICopilotAgent.RunMorningTriageAsync` を実装
- フロー
  1. `CommitIngestionService.IngestAsync()` で取り込み(`IngestionReport` を Activity で記録)
  2. `CopilotSessionFactory.CreateSessionAsync(SessionPurpose.MorningTriage)` でセッション開始
  3. `SystemMessageMode.Append` で「日本語、無視リスト、ブーストリストを尊重」プロンプトを足す
  4. `radar_list_commits(status=Unseen, limit=50)` → 必要に応じて `radar_get_diff` → `radar_score_commit` の連鎖を Copilot に任せる
  5. `SessionIdleEvent` を待って終了
- キャンセル: `CancellationToken` でセッションを `AbortAsync`、進行中の ingestion も中止

### 15.3 テスト

`App.Tests/Copilot/MorningTriageSessionTests.cs`(`ICopilotSessionFactory` をフェイクに差し替え、SDK 本体は呼ばない)

| テスト | 内容 |
|---|---|
| `Run_Ingests_Then_Starts_Session` | `IngestAsync` → `CreateSessionAsync` の順序 |
| `Run_Sends_Triage_Prompt` | `SendAsync` の prompt に「Morning Triage」マーカー |
| `Run_Waits_For_Idle` | フェイクが `SessionIdleEvent` 発火するまで待ち、それ以降に戻る |
| `Run_Cancellation_Aborts_Session` | CT キャンセルで `AbortAsync` が呼ばれる |
| `Run_Error_Propagates_From_Session` | フェイクが `SessionErrorEvent` を出すと例外 |

### 15.4 完了基準

- 上記 5 件が緑
- 手動スモーク: 実 SDK で 5 コミットを処理して `Scoring` 行が 5 件入る(`Category=Manual`)

---

## Step 16. Review UI(注目 / 保留 / 見送り候補 / アーカイブ / Ignore)

### 16.1 目的

サイドバー / コミット詳細で 注目 / 保留 / アーカイブ / Ignore Directory を行えるようにし、Sidebar カウンタが即時更新される。

### 16.2 スコープ

- `RepoSyncRadar.App/Components/ReviewActions.razor` — 4 ボタン + Reason 入力モーダル
- `ReviewActions` の `OnReviewed` で `IRadarRepository.SetReviewAsync` を呼び、`IReviewBroadcaster`(`event` を持つだけのシングルトン)で他コンポーネントへ通知
- Ignore Directory は `IgnoreRule` を追加し、関連未確認を `Rejected(reason="auto-ignored")` に一括更新
  - `Rejected` は Triage / Ignore による自動見送り候補
  - `Archived` はユーザーが手動でアクティブな確認対象から外したアーカイブ

### 16.3 テスト

`App.Tests/Components/ReviewActionsTests.cs`(bUnit)

| テスト | 内容 |
|---|---|
| `Adopt_Click_Calls_Repository` | 注目ボタンで repository スタブが Adopted で呼ばれる |
| `Archive_Requires_Reason` | Reason 未入力で disabled |
| `Later_Sets_Status_And_Closes` | 保留ボタンで Later 状態が立つ |
| `Ignore_Dir_Calls_Both_Apis` | `IgnoreRule.Add` と `bulk update Rejected` の両方 |
| `Sidebar_Receives_Broadcast` | `IReviewBroadcaster` が発火するとサイドバー再レンダリング |

### 16.4 完了基準

- 上記 5 件が緑
- 手動: シードデータで注目へ移動 → サイドバーの「注目」カウンタが +1

---

## Step 17. Adoption セッション + 共有文案

### 17.1 目的

注目したコミットに対して **Twitter / Teams / 顧客向け** の共有文案を Adoption セッションで生成。`Drafts` テーブルに保存して UI に表示。

### 17.2 スコープ

- `RepoSyncRadar.App/Copilot/AdoptionSession.cs` が `ICopilotAgent.GenerateDraftsAsync` を実装
- プロンプトに「過去 5 件の注目例(few-shot)」を含める
- JSON Schema を `responseFormat` 相当で渡す(Copilot SDK の `Sampling`/`OutputSchema` を使う)
- `Draft` 3 行(channel = twitter/teams/customer)を `radar_post_draft` 経由で保存
- 出力 UI: `RepoSyncRadar.App/Components/DraftsPanel.razor`(Copy / Regenerate)

### 17.3 テスト

`App.Tests/Copilot/AdoptionSessionTests.cs`

| テスト | 内容 |
|---|---|
| `Generate_Returns_Three_Drafts` | フェイク Session が 3 媒体を含む JSON を返すと `DraftBundle` が埋まる |
| `Generate_Persists_All_Three_Drafts` | DB に 3 行 |
| `Generate_Includes_FewShot_Examples` | プロンプトに過去注目例の SHA が含まれる(5 件まで) |
| `Generate_Rejects_Unadopted_Commit` | `ReviewStatus.Adopted` 以外は `InvalidOperationException` |
| `Generate_Truncates_When_Diff_Too_Large` | 50 KB を超える diff は切り詰め + 注記 |

`App.Tests/Components/DraftsPanelTests.cs`

| テスト | 内容 |
|---|---|
| `Renders_Three_Sections` | Twitter/Teams/Customer の 3 セクション |
| `Copy_Button_Invokes_Clipboard` | `IClipboard` スタブが呼ばれる |
| `Regenerate_Calls_AdoptionSession_Again` | `ICopilotAgent.GenerateDraftsAsync` が再度呼ばれる |

### 17.4 完了基準

- 上記 8 件が緑
- 手動: 実 SDK で 1 コミットを注目へ移動 → 3 媒体下書き生成 → Clipboard コピー OK

---

## Step 18. `radar_query` と Ask Palette

### 18.1 目的

自然言語フィルタを SELECT 限定 SQL に落とし、`radar_query` ツールで実行。SQL インジェクションと暴走を絶対に許さない。

### 18.2 スコープ

- `RepoSyncRadar.Core/Services/SqlGuard.cs` — `Validate(sql, parameters)` で
  - 単一文のみ
  - `SELECT` で始まる
  - `INSERT/UPDATE/DELETE/DROP/ATTACH/PRAGMA/...` を全て禁止
  - 参照可能なテーブル/カラムを allow-list に制限
  - `LIMIT` が無ければ強制で `LIMIT 100` を付ける
- `radar_query` ツール: `SqlGuard` 通過後に `Microsoft.Data.Sqlite` を直接叩く(EF Core を経由しない=高速&安全)
- `RepoSyncRadar.App/Components/AskPalette.razor` — Ctrl+K で開く、`ICopilotAgent.AskAsync` を呼ぶ
- `AskSession` は `radar_query` を **唯一の** ツールとして登録、`SystemMessageMode.Customize` でスキーマを Replace

### 18.3 テスト

`Core.Tests/Services/SqlGuardTests.cs`

| ケース | 期待 |
|---|---|
| `SELECT * FROM Commits LIMIT 5` | OK |
| `INSERT INTO Commits ...` | reject |
| `SELECT * FROM Commits; DROP TABLE Commits` | reject(複数文) |
| `SELECT * FROM SecretTable` | reject(allow-list 外) |
| `SELECT * FROM Commits` (LIMIT 無し) | 末尾に `LIMIT 100` を付与して OK |
| `pragma table_info('Commits')` | reject |
| `ATTACH DATABASE ...` | reject |
| 大文字小文字混在 / コメント (`--`, `/* */`) で隠した DDL | reject |
| 値が `?` パラメータでバインド | パラメータが解釈される |

`App.Tests/Copilot/AskSessionTests.cs`

| テスト | 内容 |
|---|---|
| `AskAsync_Returns_Formatted_Rows` | フェイクが `radar_query` を 1 回呼ぶ → 結果が Markdown テーブルに |
| `AskAsync_Hides_Sql_From_User_By_Default` | 既定は結果のみ。`debug=true` で SQL も返す |
| `AskAsync_Rejects_Write_Like_Prompt` | プロンプトに「すべて削除して」と書くと、`radar_query` が reject → エージェントがメッセージで応答 |

### 18.4 完了基準

- SqlGuard 9 件 + AskSession 3 件が緑
- 手動: Ctrl+K → 「先月の Copilot Workspace 関連の重要変更」→ 結果テーブル表示

---

## Step 19. ローカルプレビュー(bare clone + worktree)

### 19.1 目的

Phase 6 相当。PR HEAD の見た目を `Before` / `After` で並べる。

### 19.2 スコープ

- `RepoSyncRadar.Core/Services/Preview/DocsWorktreeManager.cs`
  - `EnsureBareCloneAsync()` — 存在しなければ `git clone --bare`
  - `FetchPrAsync(int pr)` — `git fetch origin +refs/pull/N/head:refs/pull/N/head`
  - `CheckoutAsync(string sha)` → worktree のパスを返す
  - LRU で `MaxWorktrees` を超えたら古いものを `git worktree remove --force`
- `RepoSyncRadar.Core/Services/Preview/PreviewServerHost.cs`
  - sidecar `next dev` を子プロセスで起動、ポート割当
  - プロセスは `IAsyncDisposable` で確実に終了
- 設定が空ならこの機能はオフ。失敗してもアプリ全体は起動する(Graceful degradation)

### 19.3 テスト

`Integrations.Tests/Preview/DocsWorktreeManagerTests.cs`

`git` をシステムから呼ぶので、`IProcessRunner` を抽象化してスタブする方針(本物の git は使わない)。

| テスト | 内容 |
|---|---|
| `EnsureBareCloneAsync_Skips_When_Exists` | `Directory.Exists` true で `clone` を呼ばない |
| `FetchPrAsync_Builds_Correct_Refspec` | `+refs/pull/123/head:refs/pull/123/head` |
| `CheckoutAsync_Reuses_Existing_Worktree` | 同じ SHA で 2 回呼んでも `worktree add` は 1 回 |
| `Lru_Evicts_Oldest` | `MaxWorktrees=3` で 4 つ目を追加 → 最古が remove される |
| `Disabled_When_Path_Empty` | `Options.BareCloneDir` が空文字なら全メソッドが no-op |

`Integrations.Tests/Preview/PreviewServerHostTests.cs`

| テスト | 内容 |
|---|---|
| `Start_Spawns_Process_With_Port` | `IProcessRunner` のスタブが正しい引数を受ける |
| `DisposeAsync_Kills_Process` | プロセス kill が呼ばれる |

### 19.4 完了基準

- 上記 7 件が緑
- 手動: `appsettings.Local.json` に `DocsRepository` セクションを書いた状態で、PR HEAD を Before/After 表示できる

> **補足 (2026-05-14):** Step 19 では Core 層の `DocsWorktreeManager` / `PreviewServerHost` および単体テストまでが範囲。UI 配線 (ボタン → coordinator → WebView2) は Step 19.5 で扱う。

---

## Step 19.5. ローカルプレビューの UI 配線

### 19.5.1 目的

Step 19 で完成した Core サービスを **実際に画面から呼べる** ようにする。コミット詳細ペインに「ローカルプレビュー」ボタンを置き、bare clone + worktree + sidecar 起動を 1 クリックで実行し、右側の WebView2 に `http://localhost:{port}/...` を表示する。

### 19.5.2 スコープ

- `RepoSyncRadar.Core/Services/Preview/PreviewPathMapper.cs` — `content/foo/bar.md` → `/en/foo/bar`、`content/foo/index.md` → `/en/foo`。純粋関数。`data/...` や frontmatter 不要パスは `null`。
- `RepoSyncRadar.Core/Services/Preview/PreviewSession.cs` — アクティブなプレビューの port 状態を保持するシングルトン。`Activate(int port)` / `Deactivate()` / `IsAllowed(Uri)` (loopback + 同 port のみ通す)。WebView2 リソースフィルタが allow-list の OR として使う。
- `RepoSyncRadar.Core/Services/Preview/PreviewCoordinator.cs` — `IPreviewCoordinator.PreparePreviewAsync(int prNumber, string sha, string filePath, CancellationToken)` を実装:
  1. `DocsWorktreeManager.EnsureBareCloneAsync`
  2. `DocsWorktreeManager.FetchPrAsync(prNumber)`
  3. `DocsWorktreeManager.CheckoutAsync(sha)`
  4. 同 SHA で前回起動していなければ `PreviewServerHost.StartAsync(worktree, port)` で sidecar 起動
  5. `PreviewPathMapper` で `filePath` → URL パスを解決 (失敗時はトップへフォールバック)
  6. `PreviewSession.Activate(port)` を呼んで allow-list を開ける
  7. `PreviewLink(Uri Url, int Port, string WorktreePath)` を返す。disabled なら `null`。
- `RepoSyncRadar.App/Components/IPreviewNavigator.cs` — Razor → WPF 用の event-based pub/sub (`IReviewBroadcaster` と同じパターン)。`event EventHandler<Uri>? Requested;` + `Publish(Uri)`。
- `RepoSyncRadar.App/Components/PreviewActions.razor` — 選択中コミットの隣に表示するボタン。クリックで `IPreviewCoordinator` を呼び、戻り値の URL を `IPreviewNavigator.Publish` へ。状態 (idle / running / error) と最終 URL を表示。
- `RepoSyncRadar.App/MainWindow.xaml.cs` — `IPreviewNavigator.Requested` を購読し、`Dispatcher` 経由で `DocsView.Source` を差し替え。`OnWebResourceRequested` を `_allowList.IsAllowed(uri) || _previewSession.IsAllowed(uri)` に変更。
- DI 登録: Core 側 (`PreviewSession`, `PreviewCoordinator`) と App 側 (`IPreviewNavigator`) を追加。

### 19.5.3 テスト

`Core.Tests/Services/Preview/PreviewPathMapperTests.cs`

| テスト | 内容 |
|---|---|
| `Maps_Markdown_To_Url` | `content/copilot/about-copilot.md` → `/en/copilot/about-copilot` |
| `Strips_Index_Segment` | `content/actions/index.md` → `/en/actions` |
| `Returns_Null_For_Non_Content_Path` | `data/release-notes/3.10.md` → `null` |
| `Returns_Null_For_Empty_Body` | `content/index.md` → `/en` (ルート扱い) |

`Core.Tests/Services/Preview/PreviewSessionTests.cs`

| テスト | 内容 |
|---|---|
| `Inactive_Blocks_All` | `Activate` 前は `IsAllowed` がすべて `false` |
| `Active_Allows_Matching_Localhost` | port 4500 で `Activate` → `http://localhost:4500/en/foo` が `true` |
| `Active_Blocks_Different_Port` | port 4500 で `Activate` → `http://localhost:5000/...` が `false` |
| `Active_Blocks_External_Host` | port 4500 で `Activate` → `https://docs.github.com/...` が `false` |

`Integrations.Tests/Preview/PreviewCoordinatorTests.cs`

| テスト | 内容 |
|---|---|
| `Disabled_Returns_Null` | `BareCloneDir` 空文字 → `null` を返し他のサービスは呼ばれない |
| `Runs_Steps_In_Order` | Ensure → Fetch → Checkout → Start の呼び出し順を `Received.InOrder` で検証 |
| `Same_Sha_Skips_Server_Restart` | 連続 2 回呼んでも `StartAsync` は 1 回だけ |
| `Different_Sha_Restarts_Server` | SHA を切り替えると `StartAsync` が 2 回呼ばれる |
| `Maps_File_Path_To_Url` | `filePath = content/foo/bar.md` → 戻り値の `Url.PathAndQuery` が `/en/foo/bar` |
| `Activates_Preview_Session` | 成功時に `PreviewSession.IsAllowed(http://localhost:port)` が `true` になる |

`App.Tests/Components/PreviewNavigatorTests.cs`

| テスト | 内容 |
|---|---|
| `Publish_Raises_Event_With_Uri` | サブスクライバが `Uri` を受け取る |
| `Publish_With_Null_Throws` | `ArgumentNullException` |

### 19.5.4 完了基準

- 上記 16 件が `Category!=Manual` で緑
- `dotnet build -warnaserror` 緑
- 手動: `appsettings.Local.json` に `DocsRepository` セクション + bare clone 済みのローカルパスを書いた状態で、コミット選択 → 「ローカルプレビュー」 → 右ペインに preview 画面が表示される

---

## Step 19.6. ローカルプレビューの起動時間を抜本短縮

### 19.6.1 目的

Step 19.5 完了後、ユーザから「プレビューが利用できるようになるまでの時間がかかりすぎ」というフィードバック。コールドスタートで「ローカルプレビュー」を押してから WebView2 が表示されるまでに、bare clone (1〜2 分) + `git fetch` (10〜30 秒) + `npm install` (30 秒〜1 分) + Next.js 初回コンパイル (10〜30 秒) が直列で積み上がっていた。各フェーズを並列化・先取り・共有することで、ユーザ視点の体感待ち時間を「Next.js コンパイル時間 + α」まで圧縮する。

### 19.6.2 スコープ(5 イテレーション分)

1. **比較プレビューサーバの並列起動** — `PreviewCoordinator.PrepareComparisonPreviewAsync` で before / after の `PreviewServerHost.StartAsync` を `Task.WhenAll` で同時実行 (どちらも単一プロセスを持つ別インスタンス)。冷スタート時間が概ね半分に。
2. **`node_modules` ジャンクション共有** — `INodeModulesShareManager` + `NodeModulesShareManager` を新設。`<WorktreeRoot>/.shared-node-modules/<package-lock SHA-256 先頭 16hex>/node_modules` を Windows directory junction (`cmd /c mklink /J`) で各 worktree に張る。lockfile が同一なら 2 つ目以降の worktree は `npm install` を完全スキップ。`.complete` センチネルを書く前にプロセス内 `SemaphoreSlim` ゲートで並列インストールを直列化。失敗時は通常の `npm install` にフォールバック。
3. **App 起動時の bare clone プリウォーム** — `IPreviewCoordinator.PrewarmAsync(CT)` を追加し、`App.OnStartup` で fire-and-forget。初回起動時の `git clone --bare` (github/docs で 1〜2 分) を背景で済ませる。失敗は `LogPreviewPrewarmFailed` でログのみ。
4. **PR 選択時の予測プリウォーム** — `IPreviewCoordinator.PredictivePrewarmAsync(prNumber, sha, CT)` を追加。`PreviewActions.OnParametersSet` で `_activePreviewSha` が変わるたびに fire-and-forget で発火 (前回ぶんは `CancellationTokenSource.Cancel`)。`EnsureBareCloneAsync` + `FetchPrAsync` までを進める (`CheckoutAsync` は本筋経路と Dictionary を共有するので含めない)。ユーザがボタンを押した時点で `git fetch` が完了済み。
5. **インストール / コンパイル フェーズ可視化** — `PreviewServerHost.StartAsync` に `IProgress<string>?` 引数を追加。`node_modules` 不在時は「node_modules を準備中… (このリポジトリでの初回は数分かかります)」を、その後常に「Next.js dev サーバを起動中…」を順に Report。`PreviewCoordinator` から `progress` をそのまま渡して、ユーザは「まだ install 中」と「Next.js が起動中」を区別できる。

### 19.6.3 テスト

`Integrations.Tests/Preview/PreviewCoordinatorTests.cs` (+ 6 件)

| テスト | 内容 |
|---|---|
| `PrepareComparisonPreviewAsync_Starts_Before_And_After_Servers_In_Parallel` | `Barrier(2)` を使い、両 `StartAsync` が同時に probe へ到達することを検証 |
| `PrewarmAsync_Runs_Bare_Clone_Eagerly` | `PrewarmAsync` 1 回呼ぶと `git clone --bare` が記録される |
| `PrewarmAsync_When_Disabled_Is_NoOp` | `BareCloneDir`/`CloneUrl` が空のときランナーは一切呼ばれない |
| `PredictivePrewarmAsync_Runs_Clone_And_Fetch_But_Not_Worktree` | `git clone --bare` と `git fetch origin +refs/pull/N/head:...` が記録され、`git worktree add` は呼ばれない |
| `PredictivePrewarmAsync_When_Disabled_Is_NoOp` | パイプライン無効時はランナー無起動 |
| `PredictivePrewarmAsync_Swallows_Fetch_Failure` | `git fetch` が exit 128 でも例外を投げない (ベストエフォート契約) |

`Integrations.Tests/Preview/NodeModulesShareManagerTests.cs` (新規, 5 件)

| テスト | 内容 |
|---|---|
| `EnsureAsync_Without_PackageLock_Falls_Back_To_Install` | `package-lock.json` 無し → コールバック (`npm install`) を直接呼ぶ |
| `EnsureAsync_Without_WorktreeRoot_Falls_Back_To_Install` | オプションの `WorktreeRoot` が空 → 同上 |
| `EnsureAsync_First_Time_Links_And_Runs_Install` | 初回は junction を作り `installFallback` を 1 回呼んで `.complete` を書く |
| `EnsureAsync_Second_Worktree_Reuses_Junction_Skipping_Install` | 同一 lockfile の 2 個目 worktree は `installFallback` を呼ばない |
| `EnsureAsync_Falls_Back_When_Junction_Creation_Fails` | `mklink /J` が exit≠0 → コールバックを呼んでも `.complete` は書かない |

`Integrations.Tests/Preview/PreviewServerHostTests.cs` (+ 2 件)

| テスト | 内容 |
|---|---|
| `StartAsync_Reports_Dev_Server_Progress_On_Warm_Path` | `node_modules` 既存 → "Next.js" メッセージのみ Report |
| `StartAsync_Reports_Install_Phase_When_Node_Modules_Missing` | `node_modules` 不在 → "node_modules" と "Next.js" の両方が順に Report |

### 19.6.4 完了基準

- 新規 13 件 + 既存テスト = `Category!=Manual` で 439/439 緑
- `dotnet build -warnaserror` 緑
- 手動: コールド (キャッシュ全削除) でアプリ起動 → PR を選択 → 数十秒後に「ローカルプレビュー」クリック → "Next.js dev サーバを起動中…" 表示後、Next.js コンパイル分のみで preview が表示される。冷スタートのチェーン (`git clone --bare` + `git fetch` + `npm install`) が体感ゼロになっている

---

## Step 19.7. 対象ページのインプロセス・レンダリング (Next.js バイパス)

### 19.7.1 目的

Step 19.6 を 5 イテレーション回しても、`content/**/*.md` の比較プレビューは Next.js dev サーバの初回コンパイル (10〜30 秒) が hot path に残るため、ユーザ視点では依然として「遅い」と感じる。github/docs の重い Next.js ランタイムを丸ごとロードする必要はなく、**変更が入った 1 ファイルだけ** を Markdig で in-process レンダリングできれば、起動コストは数十ミリ秒オーダーまで落ちる。Step 19 以前から非 content 配下の Markdown 専用に存在していた `MarkdownPreviewRenderer` + `LocalPreviewContentServer` の経路を、`content/` 配下の Markdown まで拡張する。

### 19.7.2 スコープ

1. **ルーティングの切替** — `PreviewActions.razor` と `CommitDetail.razor` の発火条件を以下に変更。
    - `PreviewPathMapper.IsMarkdown(path)` が真 → `PrepareMarkdownComparisonPreviewAsync` (Markdig 経路)。
    - それ以外で `PreviewPathMapper.Map(path, lang)` が非 null → `PrepareComparisonPreviewAsync` (Next.js 経路、将来の非 .md 用に温存)。
    - これにより `content/**/*.md` も Markdig 経路へ流れ、Next.js dev サーバを一切触らない。
2. **`MarkdownPreviewRenderer` の強化** — `content/` の Markdown は Jekyll/Liquid 風 frontmatter + Liquid タグを含むため、そのままだと title が抽出されず Liquid タグが生のまま表示される。以下を追加。
    - **Frontmatter 解析**: ファイル先頭の `---` 〜 `---` ブロックを抽出し、`title` / `intro` スカラーをトップレベルから取り出す (ネスト行はスキップ、`'…'` / `"…"` を unquote)。残りの本文だけを Markdig に渡す。
    - **Liquid プレースホルダ化**: `{% … %}` (ブロック) と `{{ … }}` (変数) を `<span class="rsr-liquid" title="…">…</span>` に置換 (`[GeneratedRegex]` + `RegexOptions.Singleline`)。Markdig が誤って解釈するのを防ぎつつ、ソースをそのまま視認できる。
    - **ヘッダ表示**: `<h1>{title}</h1>` + `<p class="rsr-meta">{label} · {sha8}</p>`。title がパスと異なる場合のみ `<p class="rsr-path">{repoPath}</p>` を併記。`intro` があれば lead paragraph として表示。
    - **CSS**: `.rsr-path` (淡色 monospace)、`.rsr-intro` (太字 lead)、`.rsr-liquid` (淡黄ハイライト、dark-mode 対応) を style ブロックに追加。
3. **テストの再配線** — 既存 13 件の `App.Tests` (PreviewActions / CommitDetail) は `content/**/*.md` 経路を `PrepareComparisonPreviewAsync` でモックしていたため、`PrepareMarkdownComparisonPreviewAsync` に置換 (filePath は non-nullable なので `Arg.Any<string?>()` → `Arg.Any<string>()` も連動)。検証の本意 (ボタン押下 → URL 公開、ファイル切替、プログレス、エラー、キャンセル等) は不変。

### 19.7.3 テスト

`Core.Tests/Services/Preview/MarkdownPreviewRendererTests.cs` (新規, 9 件)

| テスト | 内容 |
|---|---|
| `Renders_Title_From_Frontmatter_As_H1` | `title: About GitHub Copilot` → `<h1>About GitHub Copilot</h1>` |
| `Renders_Intro_As_Lead_Paragraph_When_Provided` | `intro:` 行があれば `class="rsr-intro"` の `<p>` |
| `Strips_Frontmatter_Block_From_Body` | `versions:` / `fpt:` 等のメタ行は本文 HTML に出ない |
| `Placeholds_Liquid_Block_Tags` | `{% ifversion fpt %}` 等が `rsr-liquid` の `<span>` 内に入り、本文段落 `<p>{%` が漏れない |
| `Placeholds_Liquid_Variable_Tags` | `{{ data variables.product.prodname_copilot }}` も同様にプレースホルダ化 |
| `Renders_Plain_Markdown_Without_Frontmatter` | frontmatter なし → Markdig の AutoIdentifier により `<h1 id="hello">Hello</h1>` (ヘッダ部の `<h1>CHANGELOG.md</h1>` は別途確認) |
| `Falls_Back_When_Markdown_Is_Null` | side が存在しない場合は `rsr-empty` + 「この時点にはファイルがありません」 |
| `Extracts_Title_With_Various_Quote_Styles` (Theory ×3) | `'Quoted'` / `"Double quoted"` / `Bare title` のいずれも正しく unquote |

`App.Tests/Components/PreviewActionsTests.cs` (11 件) / `CommitDetailTests.cs` (4 件) — モックのターゲットメソッドを `PrepareMarkdownComparisonPreviewAsync` に差し替え (テスト本意は不変)。

### 19.7.4 完了基準

- 新規 9 件 + 既存テスト総数 = `Category!=Manual` で 449/449 緑 (Integrations 59 + Core 168 + App 210 + E2E 12)
- `dotnet build -warnaserror` 緑
- 手動: コールド起動でも `content/**/*.md` の比較プレビューが Next.js を一切起動せず、サブ秒で表示される。Liquid タグはハイライト span として可視化される

---

## Step 19.8. 公式 docs へのジャンプ + Liquid タグの可視化

### 19.8.1 目的

Step 19.7 で `content/**/*.md` が Markdig + in-process でサブ秒描画できるようになったが、レビュー実務では **「この記事を公式 docs.github.com で実機確認したい」「Liquid タグが何に解決されたか hover でその場で確認したい」** という 2 軸の要求が頻発する。本ステップでは Phase 3 (公式 docs へのジャンプボタン) と Phase 4 (Liquid タグの実評価 + 可視化 MVP) を 1 commit に束ねて投入する。

### 19.8.2 スコープ

1. **OpenOfficialDocsButton (Phase 3)** — `MainWindow.xaml` のメインヘッダに「公式 docs を開く」ボタンを追加。現在 `DocsView.Source` に表示している記事 URL の path を https://docs.github.com 配下の正規 URL に解決し、既定ブラウザで開く。content path → docs URL の解決は当面 `MainWindow.BuildOfficialDocsUri` の internal static ヘルパーに置き、テストは `App.Tests` で `[InternalsVisibleTo]` 経由で行う (将来 Core 側に切り出す余地は残す)。`/content/foo/index.md` ↔ `/en/foo`、拡張子削除、URL 不可文字の `Uri.EscapeDataString` 適用などのルールを Theory でカバー。外部遷移には `UrlAllowList` を経由。
2. **DocsLiquidEvaluator MVP (Phase 4)** — `MarkdownPreviewRenderer` で行っていた素の正規表現プレースホルダ化を、軽量な Liquid プリプロセッサ `DocsLiquidEvaluator` (`Core.Services.Preview`) に置換。MVP では以下のみサポート:
    - `{{ site.data.variables.product.prodname_copilot }}` 等の **変数展開** — `data/variables/*.yml` を bare clone から読み、ドット記法で resolve。未解決変数は元の `{{ … }}` をそのまま `<span class="rsr-liquid">` で包む (Step 19.7 の振る舞いを温存)。
    - `{% data variables.… %}` の `data` タグ。GitHub Docs 独自構文だが、実質変数展開に等しい。
    - 既存の `{% ifversion … %}` / `{% link %}` 等は **そのまま `<span class="rsr-liquid">` のまま** とし (Step 19.9 で本格対応)、誤評価で本文が壊れないことを優先する。
3. **可視化 (tooltip 強化)** — `<span class="rsr-liquid">` の `title` 属性に「resolved: `<value>`」または「unresolved: `<reason>`」を載せ、hover で実評価結果が見える。値が解決できた変数は `class="rsr-liquid rsr-liquid-resolved"`、未解決は `class="rsr-liquid rsr-liquid-pending"` を付与し、CSS で淡黄 / 淡灰を分ける。

### 19.8.3 テスト

`App.Tests/MainWindowPreviewComparisonTests.cs` (拡張, +6 件) — `MainWindow.BuildOfficialDocsUri` を `[InternalsVisibleTo]` で直接呼び、Theory ベースで以下をカバー:

| テスト | 内容 |
|---|---|
| `Maps_Content_Markdown_Path_To_Public_Docs_Url` | `content/copilot/about-copilot.md` → `https://docs.github.com/en/copilot/about-copilot` |
| `Maps_Content_Index_Markdown_To_English_Home` | `content/index.md` → `https://docs.github.com/en` |
| `Maps_Section_Index_Markdown_To_Section_Home` | `content/copilot/index.md` → `https://docs.github.com/en/copilot` |
| `Returns_Null_For_Null_Or_Empty_Path` | 空入力ガード (null / "" / 空白のみ) |
| `Returns_Null_For_Non_Content_Markdown_Path` | `CHANGELOG.md` / `README.md` 等は対象外 |
| `Returns_Null_For_Non_Markdown_Content_Path` | `.yml` / `.ts` 等は対象外 |

`Core.Tests/Services/Preview/DocsLiquidEvaluatorTests.cs` (新規, 8 件)

| テスト | 内容 |
|---|---|
| `Resolves_Site_Data_Variable_To_Value` | `{{ site.data.variables.product.prodname_copilot }}` → `GitHub Copilot` |
| `Resolves_Data_Tag_Variable_To_Value` | `{% data variables.product.prodname_copilot %}` 同上 |
| `Unknown_Variable_Stays_As_Pending_Span` | 未知の path → `class="rsr-liquid rsr-liquid-pending"` |
| `Ifversion_Block_Is_Preserved_As_Span` | `{% ifversion fpt %}…{% endif %}` は span で包まれるだけ (本文は中身そのまま) |
| `Link_Tag_Is_Preserved_As_Span` | `{% link /foo %}` 同上 |
| `Multiple_Variables_In_One_Line_Resolved_Independently` | 1 行に複数変数 |
| `Variable_With_Underscores_And_Dots_Resolves` | dot-separated path で多階層 resolve |
| `Yaml_Quoted_Values_Are_Unquoted_When_Resolved` | `'GitHub Copilot'` → quotes 剥がし |

`App.Tests/MainWindowPreviewComparisonTests.cs` (拡張, 上記 6 件は同ファイル内に集約)

### 19.8.4 完了基準

- 新規テスト 17 件 + 既存テスト全体が `Category!=Manual` で緑 (App.Tests 213, Core.Tests 184 程度)
- `dotnet build -warnaserror` 緑
- 手動: 任意の `content/**/*.md` プレビュー中にヘッダの「公式 docs を開く」ボタンがアクティブになり、既定ブラウザで対応 URL が開く。Liquid 変数は hover で resolved 値が見える

---

## Step 19.9. ifversion / バージョン認識 + Version ComboBox

### 19.9.1 目的

公式 docs.github.com の Version selector (fpt / ghec / ghes 3.x …) に倣い、PR レビュー時に **「Free & Pro & Team では消えるが Enterprise Cloud では残るセクション」** や **「ghes 3.21 でのみ追加されたセクション」** などを 1 つの WebView 上で切り替え表示できるようにする。Step 19.8 では `{% ifversion … %}` を span 化するに留めたが、これではレビュアーが各版で何が変わるか **見落とす** リスクが残る。「{% ifversion X %} … {% else %} … {% endif %} は 最初の分岐だけを採用」状態を解消し、**本物の docs と同様にバージョン切替で本文を変える** とともに、**この PR が影響する全バージョンを ComboBox の tooltip で可視化する** ことで見落としを構造的に防ぐ。

### 19.9.2 スコープ

1. **`DocsVersion` / `DocsVersionCatalog`** (Core) — slug (`fpt` / `ghec` / `ghes-3.21` …) と display label を持つ value object。`DocsVersionCatalog.All` で「公式 Version selector に出る候補 (fpt → ghec → ghes 降順)」を提供し、`Default = fpt`。ghes リリースリストは静的メンテ。
2. **`VersionExpressionEvaluator`** (Core) — `ifversion fpt`, `ifversion not ghec`, `ifversion ghes > 3.18`, `ifversion fpt or ghec` 等を 1 つの `DocsVersion` 文脈で `bool` に評価する。AND / OR / NOT / 比較演算子 / グルーピングを最小限サポート。
3. **`DocsLiquidEvaluator` 拡張** — Step 19.8 で MVP だった span 化を、版を引数に取る **本物のブランチ採用** に置換。`ifversion … else … endif` をパースし、`VersionExpressionEvaluator` で評価し、選ばれた分岐だけを本文に残す。未対応構文は span として温存。
4. **`DocsVersionImpactAnalyzer`** (Core) — `before` / `after` 両側の Markdown を `DocsVersionCatalog.All` の各版で render し、結果ハッシュが異なる版だけを `AffectedVersions` として返す。これにより「fpt と ghec で別々の変更が入った PR」も漏らさず列挙できる。
5. **`MarkdownPreviewRenderer` の version 認識** — `Render(side, version)` シグネチャを追加。`PreviewCoordinator.PrepareMarkdownComparisonPreviewAsync` は `DocsVersion?` を受け取り、`PreviewComparisonLink.CurrentVersion` / `AffectedVersions` に詰めて返す。ヘッダに「現在: {label}」と影響版バッジ列を表示。
6. **`IPreviewNavigator` 双方向化** — `event VersionChangeRequested(DocsVersion)` と `void RequestVersionChange(DocsVersion)` を追加し、ホスト WPF と Razor preview コンポーネントの間で版切替を pub/sub。`PreviewComparisonRequest` に `CurrentVersion` / `AffectedVersions` を生やす。
7. **WPF Version ComboBox** — `MainWindow.xaml` のヘッダ右側に `DocsVersionSelector` ComboBox を追加。Markdown プレビュー到着時のみ可視化し、`SelectedItem` を `CurrentVersion` に同期、`AffectedVersions` を tooltip で列挙。ユーザ操作時は `_previewNavigator.RequestVersionChange(v)` を broadcast → 全 `PreviewActions` / `CommitDetail` インスタンスが現在 active な path を新版で再 prepare → ヘッダの `CurrentVersion` も更新される閉ループ。
8. **モックの positional 拡張** — `IPreviewCoordinator.PrepareMarkdownComparisonPreviewAsync` に `DocsVersion?` 引数を挿入したため、既存 19 マッチャ tuple に `Arg.Any<DocsVersion?>()` を追加し、`call.ArgAt<CancellationToken>(4)` を `(5)` に更新する。

### 19.9.3 テスト

`Core.Tests/Services/Preview/DocsVersionCatalogTests.cs` (新規, 3 件) — `All` の順序固定、`Default = fpt`、`FromSlug` の正規化と未知 slug の fallback。

`Core.Tests/Services/Preview/VersionExpressionEvaluatorTests.cs` (新規, 12 件 × Theory) — `fpt`, `not fpt`, `fpt or ghec`, `ghes > 3.18`, `ghes <= 3.16`, `(fpt or ghec) and not ghes`, 未知 token、空文字、デフォルト式等。

`Core.Tests/Services/Preview/DocsLiquidEvaluatorTests.cs` (拡張, +8 件)
- `Ifversion_Selects_Then_Branch_When_True`
- `Ifversion_Selects_Else_Branch_When_False`
- `Ifversion_Without_Else_Empties_Body_When_False`
- `Nested_Ifversion_Evaluated_Correctly`
- `Ifversion_Else_If_Chain_Picks_First_True`
- `Unknown_Tag_Inside_Branch_Stays_As_Span`
- `Comparison_Operators_Are_Honored_For_Ghes`
- `Multiple_Ifversion_Blocks_In_Single_Document`

`Core.Tests/Services/Preview/DocsVersionImpactAnalyzerTests.cs` (新規, 6 件)
- `Returns_Empty_When_No_Markdown_Differs_Across_Versions`
- `Returns_Only_Affected_Versions_When_Single_Version_Differs`
- `Returns_Multiple_Versions_When_Free_And_Enterprise_Cloud_Differ_Separately` ← ユーザ要件の核
- `Considers_Before_And_After_Sides`
- `Falls_Back_To_Default_When_Render_Throws`
- `Returns_All_Versions_When_File_Added_Or_Removed`

`Core.Tests/Services/Preview/MarkdownPreviewRendererVersionTests.cs` (新規, 4 件) — `Render` が version を受けて本文 HTML を切り替えること、ヘッダにバッジ列を出すこと、未指定時は Default で render されること。

`App.Tests/Components/PreviewActionsTests.cs` (拡張, +5 件)
- `PreviewNavigator_RequestVersionChange_Raises_Event_With_Version`
- `PreviewNavigator_RequestVersionChange_Null_Throws`
- `PreviewComparisonRequest_Defaults_Version_Metadata_To_Null`
- `VersionChangeRequested_Reruns_Coordinator_With_New_Version` ← 「Enterprise Cloud と Free で別々の変更」見落とし防止の根拠テスト
- `VersionChangeRequested_Same_Version_Does_Not_Rerun`

### 19.9.4 完了基準

- 新規テスト 38 件程度 + 既存 19 マッチャ移行 + `ArgAt` index 4→5 修正で `Category!=Manual` が緑
- `dotnet build -warnaserror` 緑 (Integrations 59 + Core 245 + App 222 + E2E 12 = 538/538)
- 手動: `{% ifversion fpt %}A{% else %}B{% endif %}` を含む PR のプレビューで ComboBox を fpt ↔ ghec に切り替えると本文が A ↔ B に切替わる。ComboBox tooltip に「この PR は 2 版に影響: GitHub Free, GHEC」が表示される

---

## Step 20. 配布(Velopack)とシークレット保管(DPAPI)

### 20.1 目的

一般配布に耐える配布パスを整える。GitHub 認証は OAuth Device Flow + DPAPI 保存 token を使い、通常利用で PAT は使わない。

### 20.2 スコープ

- `RepoSyncRadar.App/Auth/DpapiGitHubTokenStore.cs` — DPAPI ベース(`%LocalAppData%\RepoSyncRadar\github-token.bin`)
  - `SaveAsync(StoredGitHubToken token)`, `LoadAsync()`, `DeleteAsync()`
- `RepoSyncRadar.App/Updates/UpdateService.cs` — Velopack で自己更新、起動時に最新化チェック
- アプリ署名は手順書(`docs/RELEASE.md`)に切り出し、コードからは触らない

### 20.3 テスト

`App.Tests/Security/CredentialStoreTests.cs`(`Category=WindowsOnly`)

| テスト | 内容 |
|---|---|
| `RoundTrip_Save_Read` | save → read で同じ文字列 |
| `Delete_Removes_Entry` | delete 後 read で null |
| `Read_When_Missing_Returns_Null` | 初回 read |

`App.Tests/Updates/UpdateServiceTests.cs`

| テスト | 内容 |
|---|---|
| `CheckForUpdates_Skips_When_Disabled` | `Options.AutoUpdate = false` |
| `CheckForUpdates_Calls_Velopack_Manager` | `IUpdateManager` スタブが呼ばれる |
| `Apply_Pending_On_Next_Start` | フラグ立てて再起動時に適用 |

### 20.4 完了基準

- 上記 6 件が緑(`WindowsOnly` カテゴリは CI で別走)
- 手動: ビルド → publish / パッケージ → インストール → 起動 → OAuth Device Flow → DPAPI 保存 token で Copilot SDK と Octokit が認証成功

---

## 付録 A — テスト命名規約

- ファイル名: `<被テストクラス>Tests.cs`
- メソッド名: `MethodUnderTest_StateUnderTest_ExpectedBehavior`(または日本語明示で `Frontmatter_Versions無し_NullResolverIsReturned` 等)
- カテゴリ: `[Trait("Category", "Manual")]` `[Trait("Category", "WindowsOnly")]` を活用
- パラメタライズは `[Theory]` + `[InlineData]` / `[MemberData]`

---

## 付録 B — テスト固定値 / フィクスチャ管理

- `tests/Fixtures/Docs/` — `docs.github.com` のレスポンス録画(`pagelist-en-fpt.json`, `article-body-copilot-about.html` 等)
- `tests/Fixtures/GitHub/` — Octokit レスポンス録画(`pull-12345.json`, `commits-12345.json`)
- 録画は `gh api` で手動取得 → 機微情報を匿名化してコミット
- フィクスチャの参照は `EmbeddedResource` で `assembly.GetManifestResourceStream` から読み込む(ファイルパスに依存しない)

---

## 進捗トラッキング

各ステップ完了時に、本ドキュメント末尾に以下を追記する:

```text
- [x] Step N — 完了日 YYYY-MM-DD, テスト件数 NN
```

> 補足: コミット SHA は `git log` 側で十分追跡できるため、本書には記録しない
> (`--amend` で自己参照不能になる問題を避ける)。

これにより、本書は「設計書」ではなく「動く実装プラン」として機能する。

### 進捗

- [x] Step 1 — テスト基盤
  - 完了日 2026-05-13, テスト件数 3
- [x] Step 2 — オプション検証
  - 完了日 2026-05-13, テスト件数 7
- [x] Step 3 — EF Core スキーマ
  - 完了日 2026-05-13, テスト件数 6
- [x] Step 4 — Frontmatter / PathToUrlResolver
  - 完了日 2026-05-13, テスト件数 9
- [x] Step 5 — DocsApiClient
  - 完了日 2026-05-13, テスト件数 8
- [x] Step 6 — DocsGitHubClient
  - 完了日 2026-05-13, テスト件数 6
- [x] Step 7 — 取り込みパイプライン
  - 完了日 2026-05-13, テスト件数 9
- [x] Step 8 — サニタイザ
  - 完了日 2026-05-13, テスト件数 8
- [x] Step 9 — Razor コンポーネント
  - 完了日 2026-05-13, テスト件数 7
- [x] Step 10 — WebView2 埋め込み
  - 完了日 2026-05-13, テスト件数 8
- [x] Step 11 — Copilot SDK ラッパ
  - 完了日 2026-05-13, テスト件数 10
- [x] Step 12 — 監査フック
  - 完了日 2026-05-13, テスト件数 5
- [x] Step 13 — 読み取りツール
  - 完了日 2026-05-13, テスト件数 9
- [x] Step 14 — 書き込みツール
  - 完了日 2026-05-13, テスト件数 10
- [x] Step 15 — Morning Triage
  - 完了日 2026-05-13, テスト件数 5
- [x] Step 16 — Review UI
  - 完了日 2026-05-13, テスト件数 5
- [x] Step 17 — Adoption + 共有文案
  - 完了日 2026-05-13, テスト件数 8
- [x] Step 18 — Ask Palette / SqlGuard
  - 完了日 2026-05-13, テスト件数 12
- [x] Step 19 — ローカルプレビュー (Core のみ)
  - 完了日 2026-05-13, テスト件数 7
- [x] Step 19.5 — ローカルプレビュー UI 配線
- [x] Step 19.6 — ローカルプレビュー起動時間の抜本短縮 (5 イテレーション)
  - 並列起動 / `node_modules` ジャンクション共有 / bare clone プリウォーム / 予測プリウォーム / フェーズ可視化
  - 新規テスト件数 13 (合計 439/439 緑)
- [x] Step 19.7 — 対象ページのインプロセス・レンダリング (Next.js バイパス)
  - `content/**/*.md` を Markdig + LocalPreviewContentServer で in-process 描画。Next.js dev サーバはバイパス
  - `MarkdownPreviewRenderer` に frontmatter (title / intro) 解析と Liquid プレースホルダ化を追加
  - 新規テスト件数 9 (合計 449/449 緑、App.Tests の 13 件はモック差し替えで再緑化)
- [x] Step 19.8 — 公式 docs ジャンプ + Liquid 評価 MVP
  - `MainWindow` に「公式 docs を開く」ボタン (`DocsArticleUrlResolver` で正規 URL 解決) を追加
  - `DocsLiquidEvaluator` で `{{ site.data.variables.* }}` / `{% data variables.* %}` を実評価、hover tooltip に resolved / pending を表示
- [x] Step 19.9 — ifversion / バージョン認識 + Version ComboBox
  - `DocsVersion` / `DocsVersionCatalog` / `VersionExpressionEvaluator` / `DocsLiquidEvaluator` (ifversion 本物採用) / `DocsVersionImpactAnalyzer` を Core に追加
  - `MarkdownPreviewRenderer` + `PreviewCoordinator` が `DocsVersion?` を受け取り、`PreviewComparisonLink.CurrentVersion` / `AffectedVersions` を返す
  - `IPreviewNavigator` を双方向化し、`MainWindow.xaml` に `DocsVersionSelector` ComboBox を追加 — fpt/ghec/ghes-3.x を切替えて本文が切替わる閉ループ完成
  - 新規テスト件数 38 + 既存 19 マッチャに `DocsVersion?` 引数を追加、合計 538/538 緑
- [ ] Step 20 — 配布 + DPAPI

---

## 付録 C — E2E テスト (Playwright over WebView2 CDP)

> Step 10 のマニュアルスモークで発覚した「Blazor が `Loading…` のまま」「サイドバーが横並びになる」「Inbox 見出しが左端で切れる」といった UI リグレッションを **自動化された E2E** で押さえます。
> WPF + WebView2 のテストには Appium / WinAppDriver もありますが、WinAppDriver は WebView2 内の DOM を覗けません。代わりに **WebView2 が Chromium ベースである** ことを使い、**Chrome DevTools Protocol (CDP)** 経由で Playwright (`Microsoft.Playwright`) から DOM を直接検査します。

### C.1 構成

| 役割 | プロジェクト / クラス | メモ |
|---|---|---|
| 子プロセス起動 + CDP ポート待機 | `tests/RepoSyncRadar.App.E2E.Tests/AppHost.cs` | `RepoSyncRadar.exe` を `psi.EnvironmentVariables` で起動。`/json/version` を 60s ポーリング |
| Playwright + 2 つの `IBrowser` 共有 | `AppHostFixture` | BlazorWebView 用と DocsView 用に CDP を 2 ポート開き、`Chromium.ConnectOverCDPAsync` で接続 |
| 全 E2E クラスで 1 アプリ共有 | `E2ETests` (`[CollectionDefinition]`) | テスト実行毎に WPF を 1 回だけ起動。WebView2 の UDF ロック残留に起因する 2 回目起動失敗を回避 |
| App 側の CDP 注入 hook | `MainWindow.xaml.cs` | 環境変数 `REPOSYNCRADAR_BLAZOR_CDP_PORT` / `REPOSYNCRADAR_DOCS_CDP_PORT` が **セットされた時だけ** `--remote-debugging-port=N` を WebView2 に渡す。本番ビルドは何も渡さない |

### C.2 セキュリティ (Production との分離)

- CDP は **環境変数が立っている時のみ** 有効。env を立てなければ `MainWindow` は WebView2 に追加引数を渡さず、本番ユーザーが意図せず CDP を晒すことはありません。
- `AppHost.ReserveFreePort` は `TcpListener(IPAddress.Loopback, 0)` で `127.0.0.1` 限定のポートを取り、それを CDP に割り当てます。LAN からは到達不能です。

### C.3 テストの設計指針

E2E は **過去に起きたバグそのもの** を assertion で固定します:

| バグ | テスト | 期待値 |
|---|---|---|
| Blazor が起動せず `Loading…` のまま | `BlazorShell_HostPage_Loading_Placeholder_Is_Replaced` | `#app` の text に `Loading…` を含まない |
| サイドバーが横並び | `SidebarItems_Are_Stacked_Vertically_Not_Inline` | 各 `sidebar-item-*` の `boundingBox.Y` が単調増加 |
| Inbox 見出しが左端で切れる | `Sidebar_Pane_Has_Horizontal_Padding_So_Headings_Are_Not_Clipped` | `.radar-sidebar-pane` の `getComputedStyle(...).paddingLeft >= 4px` |
| Razor 部分マウント | `BlazorShell_Mounts_With_Sidebar_And_CommitList` | 5 種の `sidebar-item-{status}` がすべて可視 |
| DocsView の言語切替 | `DocsView_Loads_GitHub_Docs_In_English` | `final URL ⊃ docs.github.com` かつ `/en` を含み、`document.documentElement.lang` が `en` 始まり |

### C.4 実行方法

```powershell
# 全テスト (E2E 含む)
dotnet test -- --filter-not-trait Category=Manual

# E2E のみ
dotnet test tests/RepoSyncRadar.App.E2E.Tests -- --filter-trait Category=E2E

# CI で E2E をスキップ (デスクトップセッションが無い場合)
dotnet test -- --filter-not-trait Category=Manual --filter-not-trait Category=E2E
```

E2E は **対話的デスクトップセッション** を必要とします (WebView2 の OS-window を実際に開くため)。Windows ヘッドレス CI 上ではスキップしてください。

### C.5 Playwright ドライバ

CDP **接続のみ** を行うので Playwright の付属ブラウザバイナリインストール (`playwright.ps1 install`) は不要です (`Microsoft.Playwright.dll` 内蔵のドライバスタブで足ります)。新規開発機でいきなり E2E を回してもインストール手順は発生しません。

