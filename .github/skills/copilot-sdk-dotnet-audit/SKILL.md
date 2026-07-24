---
name: copilot-sdk-dotnet-audit
description: 'Upgrade RepoSyncRadar to a newer GitHub.Copilot.SDK version or audit a related .NET preview update in the same PR. USE FOR: Copilot SDK update/upgrade, beta upgrade, GitHub.Copilot.SDK beta.N に更新, bundled Copilot CLI refresh, .NET preview release-note audit, SDK source/package diff review, API breaking-change対応, beta/.NET preview変更点をアプリに活かす, PR作成まで. Reads user-provided official URLs plus linked area release notes, updates package/version notices, reads NuGet metadata plus SDK source/tests, applies safe app improvements, validates build/tests, and opens or updates a PR.'
argument-hint: 'target SDK/.NET version or official URL, e.g. 1.0.0-beta.9 / .NET 11 Preview 5 blog; optional: PR作成 / 調査のみ / 特定領域 usage/auth/telemetry/tools'
---

# Copilot SDK and .NET Update Audit

## 役割

RepoSyncRadar の `GitHub.Copilot.SDK` を新しい version へ安全にアップデートする。単なる package bump で終えず、NuGet package metadata、generated props/targets、XML docs、SDK source/tests、アプリ側 SDK 利用を突き合わせ、必要な breaking-change 対応と beta 変更点の有効活用を小さく実装する。最後に build/test を通し、ユーザーが求めた場合は PR まで作る。

## いつ使うか

- 「`GitHub.Copilot.SDK` を `1.0.0-beta.N` にアップデートして」と言われたとき
- 「Copilot SDK beta upgrade」「SDK update」「bundled Copilot CLI を更新」などの依頼
- SDK upgrade 後に app integration、usage billing、auth、telemetry、lifecycle、tools/permissions、structured output を見直すとき
- Copilot SDK audit の追加 context として .NET preview ブログや release notes が渡され、SDK / ASP.NET Core / EF Core / WPF / libraries / runtime / C# の変更を RepoSyncRadar に反映できるか確認するとき
- beta 間の変更点を RepoSyncRadar に活かせるか調査し、良い小修正を入れるとき
- Copilot SDK 更新 PR を作成・更新するとき

## 絶対ルール

1. **ユーザー提供の公式 URL を最初に読む**。ブログ記事、release note、issue、PR、docs URL が渡されたら本文だけでなく、本文からリンクされる詳細 release notes / breaking changes / SDK docs も先に開き、関連項目をチェックリスト化してから package audit に進む。概要記事だけ読んだ扱いにしない。RepoSyncRadar の技術スタックに関係する .NET preview では、SDK だけでなく ASP.NET Core/Blazor、EF Core、WPF/.NET desktop、libraries、runtime、C# の area notes も確認する。該当 area note が存在するか分からない場合は release-notes ディレクトリや公式 docs を探し、存在しないことも根拠として残す。
2. **target version を明確にする**。ユーザー指定があればそれを使う。未指定なら NuGet prerelease を含めて候補を確認し、最新へ進めてよいか判断する。
3. **app code と突き合わせる**。SDK の changelog 感想で終えず、`src/RepoSyncRadar.App/Copilot/`、`RepoSyncRadar.Core/Options/`、settings、UI/tests を見る。
4. **public surface 優先**。内部実装だけにあるものは使える API として扱わない。experimental API は `GHCP001` 等の警告と変更リスクを明示する。
5. **機密情報を出さない**。トークン、prompt/response content、telemetry content は既定で記録・表示しない。`CaptureContent` を有効化する提案は必ずリスク付きで扱う。
6. **既存の未コミット変更を壊さない**。dirty worktree を前提に、関係ない変更は戻さない。
7. **実装は小さく根本に当てる**。package bump、version notice、SDK 契約との不一致、設定の未配線、安全な beta 新機能活用に絞る。
8. **見送りも根拠を残す**。公式 release notes の項目を採用しない場合は、該当コード検索結果と「なぜこのアプリでは不要か」を PR 本文または完了報告に書く。

## 手順

### 1. 作業範囲と git 状態を確認する

1. `git status --short --branch` で現在の branch と dirty files を確認する。
2. ユーザーが PR 作成まで求めている場合は、必要に応じて作業 branch を作る。既に SDK update PR branch 上ならその branch を使う。
3. 未コミット変更がある場合、関係するファイルだけ読み、ユーザー変更を戻さずに作業する。

### 2. 現在版と target 版を確認する

1. ユーザーが渡した公式 URL をすべて読む。概要ブログの場合は、記事内の release notes / breaking changes / SDK-specific notes へのリンクも辿る。リンク抽出で公式記事の全 area link を拾い、手で見た項目だけに限定しない。
2. .NET preview が関係する場合は、次の area notes を明示的に確認する。存在しない/空の場合もその事実を記録する: SDK、ASP.NET Core/Blazor、EF Core、WPF/.NET desktop、libraries、runtime、C#。RepoSyncRadar では WPF + BlazorWebView + EF Core SQLite + WebView2 + release packaging の観点を必ず含める。
3. 読んだ公式情報から「採用候補」「破壊的変更」「このリポジトリでは対象外」のチェックリストを作る。例: .NET preview なら各 area note の見出しを、`Directory.Build.props`、`.csproj`、release scripts、GitHub Actions、Blazor components、EF migrations、WPF host、docs と突き合わせる。
4. `Directory.Packages.props` と project references から現在の `GitHub.Copilot.SDK` version を確認する。
5. `dotnet package search GitHub.Copilot.SDK --exact-match --prerelease --format json` で target 版の存在を確認する。
6. 変更前後の package metadata を読む。
   - `.nuspec`: version、repository URL/commit、dependencies
   - `build/GitHub.Copilot.SDK.props`: bundled `CopilotCliVersion`
   - `build/GitHub.Copilot.SDK.targets`: CLI download/copy/publish behavior
   - README / XML docs: public API surface
7. 公式 repo commit が分かる場合、`artifacts/sdk-audit/copilot-sdk` など ignored 配下に checkout/fetch して source/tests を読む。

PowerShell で `rg` が無い環境では `Get-ChildItem -Recurse` と `Select-String` を使う。

### 3. package bump と機械的追随を行う

最低限、次を更新する。

- `Directory.Packages.props`: `GitHub.Copilot.SDK` version
- `src/RepoSyncRadar.App/Settings/ThirdPartyNotices.cs`: SDK version
- `.github/copilot-instructions.md`: SDK version、bundled Copilot CLI version、安定した SDK 契約
- `GHCP001` suppression コメントなど、古い beta 番号を含む説明

更新後に `dotnet restore RepoSyncRadar.sln` を実行し、target package を NuGet cache に落とす。

EF Core / .NET SDK preview 追随で migration 生成物が変わる場合は、手編集だけで済ませない。必ず `dotnet-ef` を使い、必要なら一時 migration を `dotnet ef migrations add ...` で生成して差分を確認し、`dotnet ef migrations remove` で戻す。`remove` が直前の一時 migration を正しく消すには、その一時 migration がコンパイル対象に入っている必要があるため、`--no-build` のまま remove しない。preview SDK の analyzer が一時 migration を警告にする場合だけ、中間 build に限って `-p:TreatWarningsAsErrors=false` を使い、最終 build は通常の `-warnaserror` に戻す。

### 4. SDK source/tests の差分を読む

前後 version の repository commit がある場合は commit 間 diff を確認する。

重点ファイル:

- `dotnet/src/Session.cs`: `SendAsync`、`SendAndWaitAsync`、event handling、timeout、abort、dispose、tool execution
- `dotnet/src/Client.cs`: `CopilotClientOptions`、create/resume session、model listing、stop/force stop、auth status、mode defaults
- `dotnet/src/Types.cs`: `SessionConfig`、`MessageOptions`、`TelemetryConfig`、`SessionHooks`、`InfiniteSessionConfig`、tool/session options
- `dotnet/src/Generated/Rpc.cs` と DTOs: usage/account/quota/auth/model APIs
- E2E/unit tests: session fidelity、streaming、tools、permissions、error resilience、compaction、telemetry、per-session auth、new feature tests

確認した SDK 契約は短くメモする。例:

- `SendAndWaitAsync` は `AssistantMessageEvent?` を返す。最終テキストは `Data.Content`。
- timeout は idle 待ちの上限で、in-flight agent work の中止ではない。
- `MessageOptions` に JSON schema / response format が無い場合、JSON-only prompt だけを強保証として扱わない。
- beta.9 以降の tool filter は source-qualified (`custom:*`, `builtin:*`, `mcp:*`) を優先する。

### 5. アプリ側の SDK 利用を棚卸しする

主に以下を見る。

- `src/RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs`
- `src/RepoSyncRadar.App/Copilot/SdkCopilotSession.cs`
- `src/RepoSyncRadar.App/Copilot/SessionConfigBuilder.cs`
- `src/RepoSyncRadar.App/Copilot/RadarPermissionPolicy.cs`
- `src/RepoSyncRadar.App/Copilot/Tools/`
- `src/RepoSyncRadar.App/Copilot/Audit/ToolAuditHook.cs`
- `src/RepoSyncRadar.Core/Options/CopilotOptions.cs`
- `src/RepoSyncRadar.App/appsettings.json`、local settings UI/store
- usage/billing UI と tests

観点:

- response extraction: `ToString()` ではなく `Data.Content` か
- usage: `AssistantUsageEvent` と `Usage.GetMetricsAsync()` の両方を正しく扱っているか
- telemetry/logging: option はあるのに SDK に渡していないものがないか
- auth: 明示 token と `UseLoggedInUser = false` の意図が守られているか
- lifecycle: dispose/abort/stop の意味を取り違えていないか
- permissions: auto-approve と `skip_permission` の境界が妥当か
- tools: source-qualified tool filtering、`CopilotToolOptions`、ambient tool exposure が安全か
- structured output: SDK に schema support が無いなら防御 parser/tool strategy があるか

### 6. beta 新機能の採用可否を判断する

結果は重要度順に評価する。

1. **Correctness / user-visible failure**: breaking change、response parsing、timeout/cancel 誤解、tool filter 破綻
2. **Security / privacy**: token fallback、ambient custom instructions、org-level custom agents、prompt/response logging、telemetry content capture
3. **Observability**: SDK logger、CLI log level、OTel file exporter、request/session IDs、usage metrics、safe lifecycle/error hooks
4. **Lifecycle / reliability**: stop/force stop、idle cleanup、abort on cancel、session persistence/migration
5. **Performance / UX**: streaming/subagent streaming、permission round-trip、model fallback
6. **Future watch**: public preview 変更リスク、experimental APIs、remote sessions、canvas、quota/account APIs

採用しやすい候補:

- SDK 契約に合わせた code fix
- app settings に存在するが SDK に渡していない option の接続
- `ToolSet().AddCustom(toolName)` など public helper への置換
- 不要な ambient behavior の明示無効化 (`SkipCustomInstructions`, `CustomAgentsLocalOnly`, `CoauthorEnabled`, `ManageScheduleEnabled` など)
- version notice / tests / repo instructions の更新

慎重に扱う候補:

- OAuth scopes の既定変更。Octokit 側要件と user token strategy を確認してからにする。
- `SystemMessageMode.Replace` への変更。SDK guardrails を落とす可能性があるため、原則 `Append` 維持。
- `skip_permission` の拡大。安全性と auditability を優先する。
- `CopilotClientMode.Empty` の全面採用。`BaseDirectory` / `SessionFs` / `COPILOT_HOME` / keytar / session persistence への影響を別 PR で設計する。
- session deletion / CopilotHome migration。既存 session state を壊す可能性がある。

### 7. 実装とテスト

変更した範囲に応じてテストを追加・更新する。

- SDK option wiring: `CopilotSessionFactoryTests`
- config binding/post-configure: `OptionsValidationTests`
- local settings round-trip: `FileLocalAppSettingsStoreTests`
- session config/tool filters: `SessionConfigBuilderTests`
- usage conversion: `CopilotUsageTrackerTests`
- response parsing/fallback: `AdoptionSessionTests`
- tool permission/audit: `RadarPermissionPolicyTests`, `PermissionFlowTests`, `ToolAuditHookTests`

検証は必ず実行する。

```powershell
dotnet build RepoSyncRadar.sln -warnaserror
dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual
```

EF Core / migration 生成物を触った場合は、上記に加えて必ず次を実行する。`has-pending-model-changes` が差分ありを返したら、既存 migration designer / snapshot の target model 欠落を疑い、`dotnet-ef` で生成した probe migration の `Up` と designer を読んでから直す。

```powershell
dotnet ef migrations list --project src\RepoSyncRadar.Core\RepoSyncRadar.Core.csproj --startup-project src\RepoSyncRadar.Core\RepoSyncRadar.Core.csproj --context RadarDbContext --no-build
dotnet ef migrations has-pending-model-changes --project src\RepoSyncRadar.Core\RepoSyncRadar.Core.csproj --startup-project src\RepoSyncRadar.Core\RepoSyncRadar.Core.csproj --context RadarDbContext --no-build
```

必要に応じて先に focused test を実行する。失敗したら、今回の SDK update に関係する範囲だけ直す。

### 8. PR 作成・更新

ユーザーが PR を求めた場合:

1. `.github/pull_request_template.md` を読み、テンプレートに従う。
2. branch を push する。
3. `gh pr create` または既存 PR なら `gh pr edit` で本文を更新する。
4. PR description は英語で書く。
5. Summary には以下を含める。
   - SDK version update
   - bundled Copilot CLI version
   - 読んだ SDK 根拠 (package metadata / source commit / major source/tests)
   - 採用した app improvement
   - validation commands

## 完了レポート

最後に日本語で短くまとめる。

- 更新した SDK version と bundled Copilot CLI version
- 読んだ SDK 根拠: package version、repo commit、主要 source/tests
- 見つけた重要 finding 上位 3 件
- 実装した改善と変更ファイル
- 見送った判断事項や future watch
- 検証コマンドと結果
- PR を作った場合は URL、branch、commit

## 参考ファイル

- App SDK adapter: [`src/RepoSyncRadar.App/Copilot/SdkCopilotSession.cs`](../../../src/RepoSyncRadar.App/Copilot/SdkCopilotSession.cs)
- Session factory: [`src/RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs`](../../../src/RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs)
- Session config builder: [`src/RepoSyncRadar.App/Copilot/SessionConfigBuilder.cs`](../../../src/RepoSyncRadar.App/Copilot/SessionConfigBuilder.cs)
- Copilot options: [`src/RepoSyncRadar.Core/Options/CopilotOptions.cs`](../../../src/RepoSyncRadar.Core/Options/CopilotOptions.cs)
- Usage tracker: [`src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs`](../../../src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs)
