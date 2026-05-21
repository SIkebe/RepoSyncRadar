# Public Release Readiness

This checklist captures the current public-release blockers and must-fix follow-ups for RepoSyncRadar. It is focused on publishing the app for GitHub Enterprise Cloud administrators who use it to review `github/docs` changes for product, policy, billing, security, and operational impact.

## Status Summary

- **Release publish smoke**: `dotnet publish src/RepoSyncRadar.App/RepoSyncRadar.App.csproj -c Release -o artifacts/public-release-audit/publish -warnaserror` now succeeds.
- **Local settings leakage**: `appsettings.local.json` is ignored and not present in the publish output.
- **Dependency vulnerability scan**: `dotnet list RepoSyncRadar.sln package --vulnerable --include-transitive` reported no vulnerable packages from configured sources.
- **Runtime smoke**: `dotnet run --project src/RepoSyncRadar.App` started a responding WPF process during this audit.

## P0: Must Fix Before Any Public Release

1. **Create a signed distribution path**
   - Current state: Release publish succeeds, but there is no installer/update channel yet.
   - Required: decide between Microsoft Store, MSIX, Velopack, or another installer; sign release artifacts; document installation and upgrade behavior.
   - Evidence: no `.github/workflows` release workflow or `docs/RELEASE.md` exists yet.

2. **Add CI and release automation**
   - Required: run build, automated tests excluding Manual, vulnerability scan, and publish smoke on PRs and release tags.
   - Minimum gate:
     - `dotnet build RepoSyncRadar.sln -warnaserror`
     - `dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual`
     - `dotnet publish src/RepoSyncRadar.App/RepoSyncRadar.App.csproj -c Release -warnaserror`

3. **Verify the production OAuth App configuration**
   - Required: confirm the bundled `Copilot:OAuthClientId` matches the public RepoSyncRadar OAuth App, Device Flow is enabled, public display metadata is accurate, and no client secret is shipped or documented.
   - OAuth App description:
     - `RepoSyncRadar helps GitHub Enterprise Cloud administrators review GitHub Docs updates for product, policy, and operational changes that may affect their managed environments.`

## P1: Must Fix Before Broad Public Announcement

1. **Add public project policy files**
   - Required files: `SECURITY.md`, `SUPPORT.md`, `PRIVACY.md`, and `docs/RELEASE.md`.
   - Why: administrators need to know how to report vulnerabilities, get support, understand data handling, and verify releases.

2. **Finalize privacy and telemetry consent**
   - Current state: telemetry is off unless `TelemetryFilePath` is configured; `CaptureContent` defaults to false but can be enabled from settings.
   - Required: document what can be written when telemetry is enabled, where it is stored, how to delete it, and whether Copilot prompt/response content may be captured.
   - Recommended: add an explicit warning near `CaptureContent` in settings before broad release.

3. **Re-evaluate default OAuth scopes**
   - Current state: default `OAuthScopes` is `[ "public_repo" ]`.
   - Required: smoke test with `OAuthScopes: []`. If sync, Copilot auth, and `github/docs` public PR reads still work, prefer empty scopes. If `public_repo` is retained, document why that scope is required and what GitHub will show in the authorization prompt.

4. **Document organization approval behavior**
   - Required: explain that organizations with OAuth App access restrictions may require owner approval for the RepoSyncRadar OAuth App before organization resources can be accessed.

5. **Review local preview execution risk**
   - Current state: optional local preview can run `npm install` and `npm run dev` in a `github/docs` worktree when configured.
   - Required: keep preview disabled by default, document that it executes repository scripts locally, and consider a first-use confirmation before running install/dev commands.

## P2: Should Fix For A Polished Public Release

1. **Publish third-party notices outside the app**
   - Current state: third-party notices are available in the settings UI and covered by tests.
   - Recommended: generate or copy a `THIRD_PARTY_NOTICES.md`/`NOTICE.md` artifact for release packages.

2. **Add a release checklist**
   - Include versioning, signing, installer generation, publish smoke, app launch smoke, OAuth sign-in smoke, and rollback steps.

3. **Clarify support boundaries**
   - The app helps administrators review docs-derived signals. It does not replace GitHub Changelog, official release notes, or GitHub Support guidance.

4. **Keep docs aligned with the administrator persona**
   - README, USAGE, and DESIGN should consistently describe GitHub Enterprise Cloud administrator review, not `github/docs` maintainer workflows or personal unofficial-changelog publishing.

## Confirmed Good Signals

- `COPILOT_GITHUB_TOKEN` is the only debug token override; the app intentionally does not read `GH_TOKEN` or `GITHUB_TOKEN`.
- Copilot SDK is configured with `UseLoggedInUser = false`, so the app uses the token it obtains instead of ambient CLI sign-in state.
- OAuth tokens are stored with DPAPI under the current Windows user.
- WebView navigation uses an allow-list, and local preview routes are loopback-only.
- `appsettings.local.json` is ignored and excluded from publish output.
