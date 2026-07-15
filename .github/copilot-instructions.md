# RepoSyncRadar Copilot Instructions

RepoSyncRadar is a Windows desktop app for monitoring `github/docs` Repo sync PRs, triaging important documentation changes with the GitHub Copilot SDK, previewing rendered docs changes, and generating sharing drafts for Twitter and customer-facing notices. The solution is C#/.NET with WPF, BlazorWebView, MudBlazor, WebView2, EF Core SQLite, Octokit, and `GitHub.Copilot.SDK` 1.0.7-preview.3.

Use these repository instructions as the starting point. When code or validated behavior contradicts them, follow the verified source and update this file after confirming the rule is stable.

## Layout

- `src/RepoSyncRadar.App`: WPF startup assembly, Blazor components, Copilot SDK orchestration, preview coordination, app settings, authentication, and UI CSS under `wwwroot/css/app.css`.
- `src/RepoSyncRadar.Core`: EF Core models and `RadarDbContext`, options, service interfaces, GitHub/docs API clients, preview rendering utilities, path-to-URL resolution, and sanitization helpers.
- `tests/RepoSyncRadar.App.Tests`: bUnit/component tests, Copilot workflow tests, auth tests, preview tests, and style/contrast tests.
- `tests/RepoSyncRadar.Core.Tests`: model, data, service, resolver, preview rendering, and sanitization tests.
- `tests/RepoSyncRadar.Integrations.Tests`: integration tests for external-facing service behavior.
- `tests/RepoSyncRadar.App.E2E.Tests`: Playwright/WebView-oriented end-to-end tests. Manual tests are marked with `Category=Manual`.
- `docs`: design, usage, implementation plan, and migration notes. Read `docs/DESIGN.md` for product intent and `docs/USAGE.md` for user-visible behavior.

## Build And Test

- The pinned SDK is in `global.json`: .NET SDK `11.0.100-preview.5.26302.115` with `rollForward: latestFeature` and `allowPrerelease: true`.
- Restore/build from the repo root. Prefer PowerShell on Windows.
- Validate ordinary changes with:
  - `dotnet build RepoSyncRadar.sln -warnaserror`
  - `dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual`
- Validate GitHub Actions workflow changes with `ghalint run`; CI installs the ghalint version tracked in `tools/ghalint/go.mod` so Dependabot can update it.
- For focused test runs under xUnit v3/Microsoft.Testing.Platform, put filters after `--`. Examples:
  - `dotnet test tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj -- --filter-class RepoSyncRadar.App.Tests.Components.AppHeaderTests`
  - `dotnet test tests/RepoSyncRadar.App.E2E.Tests -- --filter-trait Category=E2E`
- Do not use legacy `dotnet test --filter "Category!=Manual"`; this repo uses Microsoft.Testing.Platform and that form can produce MTP warnings or failures.
- If App.Tests appears stale or keeps reporting an old selector, run `dotnet clean tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj` and retry the focused test.
- Some tests locate the repo root from `AppContext.BaseDirectory`; when avoiding locked `bin/`, use repo-local ignored `artifacts/...` output paths, not `%TEMP%`.

## Coding Rules

- Warnings are errors. `Directory.Build.props` enables nullable, analyzers, .NET 10 recommended analysis, SDK vulnerability/EOL checks via `CheckSdkVulnerabilities`, and code style enforcement. Keep the analyzer baseline stable during .NET 11 previews unless the PR intentionally addresses the resulting style churn.
- The app/package version is managed by `RepoSyncRadarVersion` in `Directory.Build.props`. The GitHub Actions release workflow reads that property and is intentionally manual-only (`workflow_dispatch`), so tag pushes or manually publishing an existing GitHub Release must not trigger another release build. Local one-off packaging may override it with `-p:RepoSyncRadarVersion=<semver>` through `scripts/Build-VelopackRelease.ps1`; do not set ad hoc `<Version>` values in individual projects.
- Official release assets are installer/update-feed only: pass `-NoPortable -NoLegacyManifest` to `scripts/Build-VelopackRelease.ps1` so unvalidated portable bundles and legacy Squirrel `RELEASES-*` manifests are not published.
- `scripts/Build-VelopackRelease.ps1` defaults to `-PublishMode SelfContainedPartialTrim`, which bundles .NET and asks Velopack to bootstrap only WebView2. It is not Native AOT; it uses the unsupported WPF partial-trim workaround from dotnet/wpf#3811 and needs installed-package smoke validation before broad use. Keep this mode multi-file and copy `ijwhost.dll` beside WPF's `DirectWriteForwarder.dll`; .NET 11 Preview 5 single-file payloads fail installed WebView E2E startup with `DirectWriteForwarder.dll` load errors. Pass `-PublishMode FrameworkDependent` only for explicit shared-runtime comparison builds.
- Velopack packages should explicitly pass `--icon src/RepoSyncRadar.App/Assets/AppIcon.ico` and `--shortcuts StartMenuRoot`, and installed-package smoke must verify both `current\RepoSyncRadar.exe` and the Start Menu shortcut so Windows search can find the installed app.
- Installed-package smoke cleanup must run the Velopack uninstaller before deleting `%LOCALAPPDATA%\SIkebe.RepoSyncRadar`; removing files directly can leave an orphaned Installed apps registry entry without a searchable Start Menu shortcut.
- GitHub Releases are treated as immutable once published. The Release workflow uploads Velopack assets without `--clobber`, uses draft releases for asset attachment, and can publish an existing asset-bearing draft only after validating the expected asset names. Correct bad published assets with a new `RepoSyncRadarVersion` and tag.
- Write git commit messages in English.
- Before creating or updating a pull request, read `.github/pull_request_template.md` and use every section in the PR body. Keep PR descriptions in English, and resolve each validation line by checking it or replacing it with `N/A - <reason>`. When the PR closes or resolves an issue, include the matching `Closes #...`, `Fixes #...`, or `Resolves #...` line explicitly in the Related Issues section.
- Logging must use source-generated `[LoggerMessage]`. Do not call `_logger.LogDebug/LogInformation/LogWarning(...)` extension methods directly. Use `partial sealed class` methods such as `private static partial void LogXxx(ILogger logger, ...)`.
- In xUnit tests, pass `TestContext.Current.CancellationToken` when calling cancellable APIs. For NSubstitute `Received`/`DidNotReceive`, use `Arg.Any<CancellationToken>()` or the real token.
- WPF E2E fixtures should pass a dummy `COPILOT_GITHUB_TOKEN` into the App child process so eager startup sign-in does not enter GitHub OAuth Device Flow on CI.
- Automated WPF/WebView2 tests should use `Category=E2E` only; reserve `Category=Manual` for human-operated smoke tests, not automated E2E gates.
- PR CI and release packaging should smoke-test the installed win-x64 Velopack package by setting `REPOSYNCRADAR_E2E_APP_EXE_PATH` to the installed `current\RepoSyncRadar.exe` before running the WebView E2E tests.
- App internals are already visible to `RepoSyncRadar.App.Tests` through `InternalsVisibleTo` in the App project.
- `Microsoft.NET.Sdk.Razor` does not implicitly include `System.IO` or `System.Net.Http`; add explicit `using` directives when using `File`, `Path`, `Directory`, `IOException`, `HttpClient`, or `HttpResponseMessage`.
- Do not add `System.Security.Cryptography.ProtectedData` as a package; it is already available in the target framework. Use `[SupportedOSPlatform("windows")]` where DPAPI requires it.
- Avoid `using var _ = ...`; `_` is a real variable in that context and can conflict with later discard assignments.
- C# 15 union types are enabled with `LangVersion=preview`. Use them only for small closed internal payload choices where exhaustive pattern matching removes duplicated event/result handling. Until the runtime ships `UnionAttribute` / `IUnion`, keep the App-local polyfill in `src/RepoSyncRadar.App/CompilerServices/UnionPolyfill.cs` and remove it when the target framework provides those types.
- Before finishing C# edits, check language-server diagnostics or run a focused build so IDE naming rules such as IDE1006 are caught before handoff.

## Copilot SDK And Auth

### SDK API

- The app must read Copilot SDK final assistant text from `response?.Data?.Content`, not `response?.ToString()`.
- `GitHub.Copilot.SDK` 1.0.7-preview.3 exposes `MessageOptions` prompt, attachments, mode, headers, and `DisplayPrompt`. No public JSON schema or response-format property has been observed in the XML docs.
- In `GitHub.Copilot.SDK` 1.0.7-preview.3, public C# types live under `GitHub.Copilot` / `GitHub.Copilot.Rpc`, client process settings use `CopilotClientOptions.Connection = RuntimeConnection.ForStdio(...)`, `BaseDirectory`, and `CopilotLogLevel`, and permission handlers use `Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>` with `PermissionDecision.ApproveOnce()` / `Reject(...)` / `UserNotAvailable()`.
- `GitHub.Copilot.SDK` 1.0.7-preview.3 includes opt-in `CopilotClientOptions.EnableRemoteSessions`, per-session `SessionConfig.EnableSessionTelemetry`, `MemoryConfiguration`, per-session `McpOAuthTokenStorage` (use `InMemory` unless persistent MCP OAuth tokens are explicitly required), and optional `SessionConfig.ContextTier` values `default` / `long_context`. RepoSyncRadar explicitly disables session memory unless a future feature designs a user-visible memory policy.
- `GitHub.Copilot.SDK` 1.0.7-preview.3 nupkg requests `CopilotCliVersion=1.0.71-2`, but those platform packages are not published on npm. `Directory.Build.targets` temporarily pins the effective CLI to the latest published `1.0.69`; the package `.props` overrides `Directory.Build.props`, so keep this override in `.targets` and remove it when the package becomes available.
- In 1.0.7-preview.3 hook payloads such as `PreToolUseHookInput.ToolArgs`, `PostToolUseHookInput.ToolResult`, and `PreMcpToolCallHookInput.Arguments` are JSON values; tests should create fixtures with `JsonSerializer.SerializeToElement(...)` where needed.
- In 1.0.7-preview.3 `AssistantUsageData` does not expose legacy `CopilotUsage`; use `Cost` and session `Usage.GetMetricsAsync()` for SDK-reported billing details, and keep `GHCP001` suppressions local to experimental SDK telemetry/permission/MCP auth types. `ModelBilling.TokenPrices` is available for model-picker metadata, but do not estimate AI Credits from pricing tables.
- In 1.0.7-preview.3 `CopilotSession.OpenCanvases` removes instances after `session.canvas.closed`; RepoSyncRadar does not currently register SDK canvas handlers, so no app-side state sync is required.
- In 1.0.7-preview.3 `SessionConfig.Capi.EnableWebSocketResponses` can force Copilot API Responses traffic away from WebSockets when set to `false`; RepoSyncRadar exposes this as nullable `Copilot:EnableWebSocketResponses` and leaves it unset by default to preserve SDK/runtime behavior.
- In 1.0.7-preview.3 `SessionConfig.OnMcpAuthRequest` lets hosts supply or cancel tokens for OAuth-protected MCP servers. RepoSyncRadar does not broker third-party MCP OAuth tokens, so keep a cancel handler plus `McpOAuthTokenStorage = InMemory` unless a user-visible MCP OAuth flow is designed.
- In 1.0.7-preview.3 BYOK provider callbacks are named `BearerTokenProvider` and receive `ProviderTokenArgs.SessionId`; RepoSyncRadar does not currently configure BYOK providers.
- For Copilot tool metadata such as `skip_permission` and `defer`, prefer `CopilotTool.DefineTool(..., new CopilotToolOptions { ... }, ...)` over magic-string `AdditionalProperties`. Keep RepoSyncRadar's small radar tool set pre-loaded with `CopilotToolDefer.Never`.
- For 1.0.7-preview.3 tool filtering, use `ToolSet().AddCustom(toolName)` for RepoSyncRadar's registered radar tools instead of bare tool names, and explicitly disable tool search, ambient custom instructions, org-level custom agents, coauthor trailers, scheduler integration, and session memory in `SessionConfigBuilder` unless a feature needs them.
- `SessionConfig.EnableManagedSettings` is an opt-in for enterprise managed bypass-permission policy. Leave it unset unless RepoSyncRadar deliberately adopts that policy; `false`/unset preserves existing permission prompts.

### Client And Telemetry

- Wire diagnostics through `CopilotClientOptions.Logger`, `LogLevel`, and `TelemetryConfig`; `TelemetryFilePath` is inert unless passed through SDK options. In SDK 1.0.7-preview.3, `TelemetryConfig.OtlpProtocol` maps to `OTEL_EXPORTER_OTLP_PROTOCOL` and supports `http/json` or `http/protobuf`.
- `SessionIdleTimeoutSeconds` is a client option; null or zero disables server-side idle cleanup.

### Auth Resolution

- `CopilotSessionFactory` auth resolution order is `COPILOT_GITHUB_TOKEN` debug override, in-memory cache, `IGitHubTokenStore` DPAPI store, then GitHub Device Flow. Do not read `GH_TOKEN` or `GITHUB_TOKEN` for Copilot auth.
- Release builds should ship a public, non-secret `Copilot:OAuthClientId` in `appsettings.json`; users only override it for forks or organization-managed OAuth Apps.
- Always set `CopilotClientOptions.UseLoggedInUser = false` so the app does not depend on global GitHub sign-in state.
- If `OAuthClientId` is missing and there is no stored or env token, fail clearly with `InvalidOperationException` rather than silently falling back.

### Triage Workflow

- Morning Triage is triggered from `AppHeader` through `ICopilotAgent.RunMorningTriageAsync()`. Triage/Maintenance sessions register `RadarTools.CreateAll()` and `RadarWriteTools.CreateAll()`; write-like tools are permission-gated except `radar_score_commit`, which `RadarPermissionPolicy` pre-approves for scoring persistence. Final review decisions (`radar_save_review`) are user-owned and must not be auto-written by Morning Triage, but registered Ignore rules still auto-mark future matching commits as `Rejected` during ingestion.
- `radar_score_commit` auto-marks still-unreviewed commits with score `0.44` or lower as `ReviewStatus.Rejected` with reason `auto-low-score`. Do not overwrite user-owned review states such as `Adopted`, `Later`, `Archived`, or existing `Rejected` rows.
- Triage sends need the longer `MorningTriageSession.TriageSendTimeout` through `ICopilotSession.SendAsync(prompt, timeout, ct)`; the SDK default one-minute wait is too short.
- Adoption draft/explanation generation and JSON repair sends need `AdoptionSession.DraftSendTimeout`; docs diffs plus URL fetches can exceed the SDK default one-minute wait and leave regenerated drafts empty.

## UI And Product Behavior

- Keep WPF/Blazor UI dense, operational, and scannable. This is an internal work tool, not a marketing page.
- Match existing CSS and component patterns before adding new abstractions. Do not nest decorative cards inside cards.
- For user-facing sharing drafts, Twitter and customer-facing text must include official `docs.github.com` URLs when a publishable docs URL is known. If Copilot omits the URL, preserve safety by appending it before saving.
- Commit detail should show the useful first commit message line only. Do not surface `Co-authored-by`, `Signed-off-by`, `Reviewed-by`, or `Acked-by` trailers as prominent UI text.
- Copilot usage UI must label units explicitly: AI Credits as `credits`, Premium Request cost as `PR`, request counts as `requests`, and token counts as `tokens`.
- Copilot usage UI should show AI Credits only from SDK usage events or `Usage.GetMetricsAsync()` session metrics; when SDK AI Credits are absent, leave credits unreported rather than estimating from a model pricing table.
- For Copilot fallback models, prefer currently supported non-retiring models. Check GitHub Changelog plus the supported-models docs before hardcoding model IDs; avoid `GPT-4.1`, `GPT-5`, `GPT-5.2`, and `GPT-5.2-Codex` as preferred fallbacks because they are retired or scheduled for retirement.

## Preview And WebView

### Preview Infrastructure

- Official `docs.github.com` may already match a Repo sync PR if deployed. Visual comparison should use local preview of parent SHA vs PR HEAD, not production pages.
- Markdown preview should use existing `PreviewCoordinator`, `DocsWorktreeManager`, `LocalPreviewContentServer`, and `PreviewPortAllocator` patterns instead of ad hoc git/object reading or local HTTP hosting. `NextDevServerProcessCleaner` remains only for cleaning up legacy worktrees that may be locked by old Next dev servers.
- Startup docs preview prewarm is opt-in via `DocsRepository:PrewarmOnStartup`; the default must not clone/fetch `github/docs` until a preview action or predictive prewarm needs it.

### WebView2 Behavior

- WebView2 `Source` assignment is a no-op for identical URIs. Markdown preview URLs must include content-affecting dimensions such as docs version and file path in the query. `LocalPreviewContentServer.NormalizeRoute` strips query strings for route lookup.
- The right-pane WebView opens GitHub PR/commit pages and may also host GitHub.com Copilot Chat. Keep `WebView.AllowedUrlHosts` separate from Copilot tool URL permissions, and include the GitHub Copilot Chat API hosts (`api.githubcopilot.com`, `api.business.githubcopilot.com`, and `api.enterprise.githubcopilot.com`) so chat preflight requests are not blocked as `Chat failed to load`.
- WebView2 may raise stale `NavigationCompleted` failures such as `ConnectionAborted` for a previous navigation after the app has already started a new one; gate completion handling by `NavigationId` when showing load-failure overlays, and do not accept completions before the expected `NavigationId` is known. For GitHub single-page loads, retry transient `ConnectionAborted` / `OperationCanceled` once via `about:blank`.
- For WebView2 UI validation, connect directly to the app's CDP endpoints instead of assuming Node Playwright is installed. Use the `REPOSYNCRADAR_BLAZOR_CDP_PORT` and `REPOSYNCRADAR_DOCS_CDP_PORT` targets with the Chrome DevTools Protocol to inspect/click the Blazor shell and docs preview DOM.
- For preview UI regressions tied to a specific commit, validate in the real app with `REPOSYNCRADAR_BLAZOR_CDP_PORT` and `REPOSYNCRADAR_DOCS_CDP_PORT`: select the exact commit row, open `WebView2 で開く`, inspect the docs WebView DOM for the reported artifact, and capture a screenshot under `artifacts/` before claiming the fix works.

### Markdown/Liquid Rendering

- Markdown/Liquid preview should mimic github/docs rendering. Render `{% octicon "name" ... %}` as Primer Octicons inline SVG with appropriate classes/attributes. Preserve data tag indentation, alert blocks, tool/platform blocks, prompt blocks, code tab blocks, and Copilot links where practical.
- Markdown comparison preview should read Markdown and referenced Liquid inputs from the bare clone by SHA (`git show`/`git ls-tree`) instead of creating full worktrees. For binary or static assets referenced by Markdown, extract only the needed files from the same commit into the preview asset cache so screenshots do not break.
- Markdown preview Liquid context must stay lazy but complete for the clicked file: load referenced reusables, AUTOTITLE targets, and referenced `data/**/*.yml` sequence files used by `for` loops such as `tables.copilot.models-and-pricing`; do not fall back to all-repo reusable/content scans for interactivity.
- Markdown preview should also load referenced `data/tables/**/*.yml` mapping objects and the GHES `enterpriseServerReleases` object when pages use Liquid mapping loops, `assign`, `case/when`, dynamic bracket lookups, or release-date tables.
- Markdown preview `ifversion` evaluation should load referenced `data/features/*.yml` files so known feature flags use their real `versions` mapping; unknown feature flags should remain conservatively visible.
- Rendered Markdown comparison should show a visible marker in both panes when possible; for pure additions/removals, use a small gap marker at the stable adjacent text on the side where the changed prose is absent.

### Cache And Theme

- Cache cleanup should detach/rename large worktree directories quickly and let physical deletion continue in the background so the Blazor UI is not blocked by hundreds of MB of file deletes.
- Dark-theme docs preview must keep text readable; diff highlights should be low-alpha tints with borders/outlines and `color: inherit`.

## Maintaining These Instructions

- Treat this file as living repository knowledge. During normal work, if you discover a repo-wide rule, command, SDK behavior, test workaround, UI convention, or failure mode that would save future agents time, update this file in the same change when it is stable and broadly useful.
- Treat invoked skills as living workflow knowledge too. If a task exposes a stable improvement, missing guardrail, or recurring pitfall in a relevant `.github/skills/*/SKILL.md`, update that skill in the same change without waiting for an explicit request.
- Keep additions concise and non-task-specific. Do not record one-off task details, secrets, machine-local absolute paths beyond repo examples, or temporary failures that are unlikely to recur.
- Prefer updating an existing bullet over adding a duplicate. Remove or correct instructions that become false.
- After changing this file, run at least a focused validation relevant to the change. For code changes in the same task, still run the normal build/test gate above.
