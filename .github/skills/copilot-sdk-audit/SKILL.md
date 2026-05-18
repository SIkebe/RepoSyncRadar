---
name: copilot-sdk-audit
description: 'Audit GitHub Copilot SDK usage in RepoSyncRadar. USE FOR: GitHub Copilot SDK 公式ドキュメント調査、SDK source audit、Copilot SDK beta upgrade review、usage billing/API 調査、SDK に基づく改善点探索、non-JSON response root-cause investigation, telemetry/logging/lifecycle/auth/usage improvements. Reads official NuGet/package docs and SDK source, compares them with app usage, ranks findings, implements safe scoped improvements when requested or clearly warranted, and validates with build/tests.'
argument-hint: '(任意) 調査対象: usage billing / auth / telemetry / lifecycle / tools / structured output / upgrade など'
---

# Copilot SDK Audit

## 役割

RepoSyncRadar の GitHub Copilot SDK 利用を、公式ドキュメント・NuGet package metadata・SDK source・SDK tests を根拠に監査する。単なる感想ではなく、アプリ内の実装と SDK の実際の public surface を突き合わせ、改善候補を重要度順に整理する。高確度で小さく直せるものは実装し、ビルドとテストで確認する。

## いつ使うか

- 「GitHub Copilot SDK の公式ドキュメントとソースを読んで改善点を探して」と言われたとき
- SDK upgrade 後に app integration を見直すとき
- usage billing / AI Credits / telemetry / auth / lifecycle / tool permission / structured output の SDK 対応状況を調べるとき
- Copilot response が想定外の形になった原因を SDK 契約から確認するとき
- `GitHub.Copilot.SDK` の public preview API に依存する実装を安全に棚卸ししたいとき

## 絶対ルール

1. **公式根拠を先に読む**。README、nuspec、generated props/targets、XML docs、SDK source、SDK tests のいずれかで確認するまで API が存在すると断定しない。
2. **app code と突き合わせる**。SDK の機能紹介だけで終えず、`src/RepoSyncRadar.App/Copilot/`、`RepoSyncRadar.Core/Options/`、設定ファイル、関連 UI/tests を見る。
3. **public surface 優先**。内部実装だけにあるものは、使える API として扱わない。experimental API は `GHCP001` 等の警告と変更リスクを明示する。
4. **機密情報を出さない**。トークン、prompt/response content、telemetry content は既定で記録・表示しない。`CaptureContent` を有効化する提案は必ずリスク付きで扱う。
5. **既存の未コミット変更を壊さない**。dirty worktree を前提に、関係ない変更は戻さない。
6. **実装は小さく根本に当てる**。SDK 契約との不一致、設定の未配線、lifecycle/observability の欠落など、明確な改善だけを触る。

## 手順

### 1. 調査対象を切る

ユーザーの引数や会話から主題を決める。

- `usage billing`: `AssistantUsageEvent`、`Usage.GetMetricsAsync`、AI Credits / nano AIU、quota/account RPC
- `auth`: `GitHubToken`、`UseLoggedInUser`、per-session auth、OAuth scopes
- `telemetry/logging`: `CopilotClientOptions.Logger`、`LogLevel`、`TelemetryConfig`、content capture
- `lifecycle`: `StopAsync`、`ForceStopAsync`、`DisposeAsync`、`AbortAsync`、idle timeout、session persistence
- `tools/permissions`: `AIFunction` metadata、`skip_permission`、permission result kinds、hooks
- `structured output`: `MessageOptions`、response format/schema の有無、tool-based structured output の可能性
- `upgrade`: package version、bundled CLI version、breaking changes、new public APIs

主題が曖昧でも、まず `telemetry/logging`、`lifecycle`、`auth`、`usage`、`structured output` の順で薄く見る。

### 2. SDK package の事実を確認する

1. `Directory.Packages.props` / project references から `GitHub.Copilot.SDK` の version を確認する。
2. NuGet cache または package metadata から以下を読む。
   - README / XML docs
   - `.nuspec`
   - `build/GitHub.Copilot.SDK.props` / targets
   - repository URL と commit SHA
   - bundled Copilot CLI version
3. 公式 repo が分かる場合は、package の repository commit に checkout して source を読む。

PowerShell では `rg` が無い環境があるため、その場合は `Get-ChildItem -Recurse` と `Select-String` を使う。

### 3. SDK source/tests を読む

最低限、次を確認する。

- `Session.cs`: `SendAsync`、`SendAndWaitAsync`、event handling、timeout、abort、dispose、tool execution
- `Client.cs`: `CopilotClientOptions` の使われ方、create/resume session、model listing、stop/force stop、auth status
- `Types.cs`: `SessionConfig`、`MessageOptions`、`TelemetryConfig`、`PermissionRequestResultKind`、`SystemMessageConfig`、`SessionHooks`、`InfiniteSessionConfig`
- `Generated/Rpc.cs` と generated DTOs: usage/account/quota/auth/model APIs
- E2E tests: session fidelity、streaming、tools、permissions、error resilience、compaction、telemetry、per-session auth

確認した SDK 契約は短くメモする。例:

- `SendAndWaitAsync` は `AssistantMessageEvent?` を返す。最終テキストは `Data.Content`。
- timeout は idle 待ちの上限で、in-flight agent work の中止ではない。
- `Streaming = true` でも final `assistant.message` は出る。
- `MessageOptions` に JSON schema / response format が無い場合、JSON-only prompt だけを強保証として扱わない。

### 4. アプリ側の SDK 利用を棚卸しする

主に以下を見る。

- `src/RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs`
- `src/RepoSyncRadar.App/Copilot/SdkCopilotSession.cs`
- `src/RepoSyncRadar.App/Copilot/SessionConfigBuilder.cs`
- `src/RepoSyncRadar.App/Copilot/RadarPermissionPolicy.cs`
- `src/RepoSyncRadar.App/Copilot/Tools/`
- `src/RepoSyncRadar.Core/Options/CopilotOptions.cs`
- `src/RepoSyncRadar.App/appsettings.json` と local settings UI/store
- usage/billing UI と tests

SDK 契約とのズレを探す観点:

- response extraction: `ToString()` ではなく `Data.Content` か
- usage: event と metrics の両方を正しく扱っているか
- telemetry/logging: option はあるのに SDK に渡していないものがないか
- auth: 明示 token と `UseLoggedInUser = false` の意図が守られているか
- lifecycle: dispose/abort/stop の意味を取り違えていないか
- permissions: auto-approve と `skip_permission` の境界が妥当か
- structured output: SDK に schema support が無いなら防御 parser/tool strategy があるか

### 5. 改善候補をランク付けする

結果はこの順で出す。

1. **Correctness / data loss / user-visible failure**: 例 `assistant.ToString()` 返却、timeout cancellation 誤解、JSON 強保証の誤認
2. **Security / privacy**: token fallback、OAuth scopes、prompt/response logging、telemetry content capture
3. **Observability**: SDK logger、CLI log level、OTel file exporter、request/session IDs、usage metrics
4. **Lifecycle / reliability**: stop/force stop、idle cleanup、abort on cancel、session persistence
5. **Performance / UX**: streaming の必要性、permission round-trip、model fallback
6. **Future watch**: public preview 変更リスク、experimental APIs、未公開/未実装 API

各 finding には必ず、根拠・影響・推奨対応・実装可否を付ける。

### 6. 安全な改善は実装する

ユーザーが「調査だけ」と明言していない限り、高確度で小さい改善は実装してよい。

良い実装候補:

- SDK `Logger` / `LogLevel` / `TelemetryConfig` の配線
- app settings に存在するが SDK に渡していない項目の接続
- `Data.Content` extraction など SDK 契約との明確な不一致修正
- local settings store / settings UI / docs / tests の追随
- experimental API 使用箇所の `#pragma` と理由コメントの確認

慎重に扱う候補:

- OAuth scopes の既定変更。Octokit 側要件と user token strategy を確認してからにする。
- `SystemMessageMode.Replace` への変更。SDK guardrails を落とす可能性があるため、原則 `Append` 維持。
- `skip_permission` の拡大。安全性と auditability を優先する。
- session deletion / CopilotHome migration。既存 session state を壊す可能性がある。

### 7. テストと検証

変更した範囲に応じてテストを追加する。

- SDK option wiring: `CopilotSessionFactoryTests`
- config binding/post-configure: `OptionsValidationTests`
- local settings round-trip: `FileLocalAppSettingsStoreTests`
- session config: `SessionConfigBuilderTests`
- usage conversion: `CopilotUsageTrackerTests`
- response parsing/fallback: `AdoptionSessionTests`

完了前に必ず実行する。

```powershell
dotnet build RepoSyncRadar.sln -warnaserror
dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual
```

失敗したら、調査対象外のバグを広げず、今回の変更に関係する範囲で直す。既存の dirty files は戻さない。

## 完了レポート

最後に日本語で短くまとめる。

- 読んだ SDK 根拠: package version、repo commit、主要 source/tests
- 見つけた重要 finding 上位 3 件
- 実装した改善と変更ファイル
- 残した判断事項や将来 watch
- 検証コマンドと結果

## 参考ファイル

- App SDK adapter: [`src/RepoSyncRadar.App/Copilot/SdkCopilotSession.cs`](../../../src/RepoSyncRadar.App/Copilot/SdkCopilotSession.cs)
- Session factory: [`src/RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs`](../../../src/RepoSyncRadar.App/Copilot/CopilotSessionFactory.cs)
- Session config builder: [`src/RepoSyncRadar.App/Copilot/SessionConfigBuilder.cs`](../../../src/RepoSyncRadar.App/Copilot/SessionConfigBuilder.cs)
- Copilot options: [`src/RepoSyncRadar.Core/Options/CopilotOptions.cs`](../../../src/RepoSyncRadar.Core/Options/CopilotOptions.cs)
- Usage tracker: [`src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs`](../../../src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs)