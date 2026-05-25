# RepoSyncRadar Copilot Instructions

RepoSyncRadar is a Windows desktop app for monitoring `github/docs` Repo sync PRs, triaging important documentation changes with the GitHub Copilot SDK, previewing rendered docs changes, and generating sharing drafts for Twitter, Teams, and customer-facing notices. The solution is C#/.NET with WPF, BlazorWebView, MudBlazor, WebView2, EF Core SQLite, Octokit, and `GitHub.Copilot.SDK` 1.0.0-beta.7.

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

- The pinned SDK is in `global.json`: .NET SDK `10.0.300` with `rollForward: latestFeature`.
- Restore/build from the repo root. Prefer PowerShell on Windows.
- Validate ordinary changes with:
  - `dotnet build RepoSyncRadar.sln -warnaserror`
  - `dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual`
- For focused test runs under xUnit v3/Microsoft.Testing.Platform, put filters after `--`. Examples:
  - `dotnet test tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj -- --filter-class RepoSyncRadar.App.Tests.Components.AppHeaderTests`
  - `dotnet test tests/RepoSyncRadar.App.E2E.Tests -- --filter-trait Category=E2E`
- Do not use legacy `dotnet test --filter "Category!=Manual"`; this repo uses Microsoft.Testing.Platform and that form can produce MTP warnings or failures.
- If App.Tests appears stale or keeps reporting an old selector, run `dotnet clean tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj` and retry the focused test.
- Some tests locate the repo root from `AppContext.BaseDirectory`; when avoiding locked `bin/`, use repo-local ignored `artifacts/...` output paths, not `%TEMP%`.

## Coding Rules

- Warnings are errors. `Directory.Build.props` enables nullable, analyzers, latest recommended analysis, and code style enforcement.
- Logging must use source-generated `[LoggerMessage]`. Do not call `_logger.LogDebug/LogInformation/LogWarning(...)` extension methods directly. Use `partial sealed class` methods such as `private static partial void LogXxx(ILogger logger, ...)`.
- In xUnit tests, pass `TestContext.Current.CancellationToken` when calling cancellable APIs. For NSubstitute `Received`/`DidNotReceive`, use `Arg.Any<CancellationToken>()` or the real token.
- App internals are already visible to `RepoSyncRadar.App.Tests` through `InternalsVisibleTo` in the App project.
- `Microsoft.NET.Sdk.Razor` does not implicitly include `System.IO` or `System.Net.Http`; add explicit `using` directives when using `File`, `Path`, `Directory`, `IOException`, `HttpClient`, or `HttpResponseMessage`.
- Do not add `System.Security.Cryptography.ProtectedData` as a package; it is already available in the target framework. Use `[SupportedOSPlatform("windows")]` where DPAPI requires it.
- Avoid `using var _ = ...`; `_` is a real variable in that context and can conflict with later discard assignments.
- Before finishing C# edits, check language-server diagnostics or run a focused build so IDE naming rules such as IDE1006 are caught before handoff.

## Copilot SDK And Auth

### SDK API

- The app must read Copilot SDK final assistant text from `response?.Data?.Content`, not `response?.ToString()`.
- `GitHub.Copilot.SDK` 1.0.0-beta.7 exposes `MessageOptions` prompt, attachments, mode, and headers. No public JSON schema or response-format property has been observed in the XML docs.
- In `GitHub.Copilot.SDK` 1.0.0-beta.7, public C# types live under `GitHub.Copilot` / `GitHub.Copilot.Rpc`, client process settings use `CopilotClientOptions.Connection = RuntimeConnection.ForStdio(...)`, `BaseDirectory`, and `CopilotLogLevel`, and permission handlers use `Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>` with `PermissionDecision.ApproveOnce()` / `Reject(...)` / `UserNotAvailable()`.
- `GitHub.Copilot.SDK` 1.0.0-beta.7 nupkg includes `build/GitHub.Copilot.SDK.props` with `CopilotCliVersion=1.0.53-2`; do not pin `CopilotCliVersion` in `Directory.Build.props` unless a future package regresses.
- In beta.7 hook payloads such as `PreToolUseHookInput.ToolArgs` and `PostToolUseHookInput.ToolResult` are `JsonElement?`; tests should create fixtures with `JsonSerializer.SerializeToElement(...)`.
- In beta.7 `AssistantUsageData` no longer exposes `CopilotUsage`; use `Cost` and session `Usage.GetMetricsAsync()` for SDK-reported billing details, and keep `GHCP001` suppressions local to experimental SDK telemetry/permission types.
- For Copilot tool metadata such as `skip_permission`, prefer `CopilotTool.DefineTool(..., new CopilotToolOptions { SkipPermission = true }, ...)` over magic-string `AdditionalProperties`.

### Client And Telemetry

- Wire diagnostics through `CopilotClientOptions.Logger`, `LogLevel`, and `TelemetryConfig`; `TelemetryFilePath` is inert unless passed through SDK options.
- `SessionIdleTimeoutSeconds` is a client option; null or zero disables server-side idle cleanup.

### Auth Resolution

- `CopilotSessionFactory` auth resolution order is `COPILOT_GITHUB_TOKEN` debug override, in-memory cache, `IGitHubTokenStore` DPAPI store, then GitHub Device Flow. Do not read `GH_TOKEN` or `GITHUB_TOKEN` for Copilot auth.
- Release builds should ship a public, non-secret `Copilot:OAuthClientId` in `appsettings.json`; users only override it for forks or organization-managed OAuth Apps.
- Always set `CopilotClientOptions.UseLoggedInUser = false` so the app does not depend on global GitHub sign-in state.
- If `OAuthClientId` is missing and there is no stored or env token, fail clearly with `InvalidOperationException` rather than silently falling back.

### Triage Workflow

- Morning Triage is triggered from `AppHeader` through `ICopilotAgent.RunMorningTriageAsync()`. Triage/Maintenance sessions register `RadarTools.CreateAll()` and `RadarWriteTools.CreateAll()`; write-like tools are permission-gated except `radar_score_commit` and `radar_save_review`, which `RadarPermissionPolicy` pre-approves for triage scoring/review persistence.
- Triage sends need the longer `MorningTriageSession.TriageSendTimeout` through `ICopilotSession.SendAsync(prompt, timeout, ct)`; the SDK default one-minute wait is too short.

## UI And Product Behavior

- Keep WPF/Blazor UI dense, operational, and scannable. This is an internal work tool, not a marketing page.
- Match existing CSS and component patterns before adding new abstractions. Do not nest decorative cards inside cards.
- For user-facing sharing drafts, Twitter, Teams, and customer-facing text must include official `docs.github.com` URLs when a publishable docs URL is known. If Copilot omits the URL, preserve safety by appending it before saving.
- Commit detail should show the useful first commit message line only. Do not surface `Co-authored-by`, `Signed-off-by`, `Reviewed-by`, or `Acked-by` trailers as prominent UI text.
- Copilot usage UI must label units explicitly: AI Credits as `credits`, Premium Request cost as `PR`, request counts as `requests`, and token counts as `tokens`.
- When SDK AI Credits are absent, fall back to the GitHub Docs model pricing table for usage estimates. Unknown models should remain unreported rather than guessed.
- For Copilot fallback models, prefer currently supported non-retiring models. Check GitHub Changelog plus the supported-models docs before hardcoding model IDs; avoid `GPT-4.1`, `GPT-5`, `GPT-5.2`, and `GPT-5.2-Codex` as preferred fallbacks because they are retired or scheduled for retirement.

## Preview And WebView

### Preview Infrastructure

- Official `docs.github.com` may already match a Repo sync PR if deployed. Visual comparison should use local preview of parent SHA vs PR HEAD, not production pages.
- Preview worktrees and npm/Next dev servers are process-sensitive. Use existing `PreviewServerHost`, `DocsWorktreeManager`, `NextDevServerProcessCleaner`, and `PreviewPortAllocator` patterns instead of ad hoc process cleanup.
- github/docs preview needs `REQUEST_TIMEOUT=600000` because Windows ARM64 first-page compilation can exceed the default 15 seconds.

### WebView2 Behavior

- WebView2 `Source` assignment is a no-op for identical URIs. Markdown preview URLs must include content-affecting dimensions such as docs version and file path in the query. `LocalPreviewContentServer.NormalizeRoute` strips query strings for route lookup.
- The right-pane WebView opens GitHub PR/commit pages and may also host GitHub.com Copilot Chat. Keep `WebView.AllowedUrlHosts` separate from Copilot tool URL permissions, and include the GitHub Copilot Chat API hosts (`api.githubcopilot.com` and `api.enterprise.githubcopilot.com`) so chat preflight requests are not blocked as `Chat failed to load`.
- For preview UI regressions tied to a specific commit, validate in the real app with `REPOSYNCRADAR_BLAZOR_CDP_PORT` and `REPOSYNCRADAR_DOCS_CDP_PORT`: select the exact commit row, open `WebView2 で開く`, inspect the docs WebView DOM for the reported artifact, and capture a screenshot under `artifacts/` before claiming the fix works.

### Markdown/Liquid Rendering

- Markdown/Liquid preview should mimic github/docs rendering. Render `{% octicon "name" ... %}` as Primer Octicons inline SVG with appropriate classes/attributes. Preserve data tag indentation, alert blocks, tool/platform blocks, prompt blocks, and Copilot links where practical.
- Markdown comparison preview should read Markdown and referenced Liquid inputs from the bare clone by SHA (`git show`/`git ls-tree`) instead of creating full worktrees; reserve full worktrees for npm/Next preview. For binary or static assets referenced by Markdown, extract only the needed files from the same commit into the preview asset cache so screenshots do not break.
- Markdown preview Liquid context must stay lazy but complete for the clicked file: load referenced reusables, AUTOTITLE targets, and referenced `data/**/*.yml` sequence files used by `for` loops such as `tables.copilot.models-and-pricing`; do not fall back to all-repo reusable/content scans for interactivity.

### Cache And Theme

- Cache cleanup should detach/rename large worktree directories quickly and let physical deletion continue in the background so the Blazor UI is not blocked by hundreds of MB of file deletes.
- Dark-theme docs preview must keep text readable; diff highlights should be low-alpha tints with borders/outlines and `color: inherit`.

## Maintaining These Instructions

- Treat this file as living repository knowledge. During normal work, if you discover a repo-wide rule, command, SDK behavior, test workaround, UI convention, or failure mode that would save future agents time, update this file in the same change when it is stable and broadly useful.
- Treat invoked skills as living workflow knowledge too. If a task exposes a stable improvement, missing guardrail, or recurring pitfall in a relevant `.github/skills/*/SKILL.md`, update that skill in the same change without waiting for an explicit request.
- Keep additions concise and non-task-specific. Do not record one-off task details, secrets, machine-local absolute paths beyond repo examples, or temporary failures that are unlikely to recur.
- Prefer updating an existing bullet over adding a duplicate. Remove or correct instructions that become false.
- After changing this file, run at least a focused validation relevant to the change. For code changes in the same task, still run the normal build/test gate above.
