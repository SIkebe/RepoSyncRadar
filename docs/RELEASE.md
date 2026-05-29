# Release

RepoSyncRadar is distributed on Windows with Velopack. Release builds are framework-dependent: the installer bootstraps the .NET Desktop Runtime and the Evergreen WebView2 Runtime, while the application payload stays smaller and receives normal runtime servicing.

## Release Model

- Installer/update framework: Velopack.
- App publish mode: framework-dependent, RID-specific, single-file app payload.
- Runtime dependencies: .NET 10 Desktop Runtime and Evergreen WebView2 Runtime.
- Channels: use architecture-specific channels such as `win-x64-stable` and `win-arm64-stable`.
- Signing: public artifacts must be signed before broad distribution. The provider is still undecided; see `docs/PUBLIC_RELEASE_READINESS.md`.

Do not use `--self-contained` for the default public installer unless an offline or highly locked-down environment specifically requires it. Self-contained builds are useful as a fallback, but they increase installer and update size and bypass normal shared runtime servicing.

## Build A Local Installer

From the repository root:

```powershell
./scripts/Build-VelopackRelease.ps1 -Runtime win-x64
```

For Windows on Arm:

```powershell
./scripts/Build-VelopackRelease.ps1 -Runtime win-arm64
```

The default app/package version is managed in `Directory.Build.props` as `RepoSyncRadarVersion`. Pass `-Version <semver>` only for one-off local smoke builds where you intentionally do not want to edit the shared version file.

The script publishes the app and writes Velopack assets under `artifacts/release/velopack/<runtime>/`. The user-facing installer is `SIkebe.RepoSyncRadar-<channel>-Setup.exe`; portable bundles are intentionally disabled for official releases because the installed path is the validated user environment. The `.nupkg` files plus `releases.<channel>.json` and `assets.<channel>.json` form the update feed. Legacy `RELEASES-<channel>` manifests are not uploaded because RepoSyncRadar has no Squirrel-era client population to support.

The Velopack package id is `SIkebe.RepoSyncRadar` so the installer root does not collide with RepoSyncRadar's existing `%LOCALAPPDATA%\RepoSyncRadar` app-data folder.

The script restores and uses the repo-local .NET tool manifest (`dotnet tool restore` / `dotnet tool run vpk`), so use `.config/dotnet-tools.json` to update the Velopack CLI version.

When rebuilding the same version/channel locally, pass `-Force` to clear the previous local Velopack output first:

```powershell
./scripts/Build-VelopackRelease.ps1 -Runtime win-arm64 -Force
```

## Signing

For local unsigned smoke tests, omit signing parameters. Public distribution must sign the app and installer artifacts. Azure Artifact Signing Basic was tested, but Public Trust identity validation is unavailable for the current Japan sold-to billing account, so do not use it as the default path.

With an existing `signtool.exe` parameter set:

```powershell
./scripts/Build-VelopackRelease.ps1 `
  -Runtime win-x64 `
  -SignParams '/td sha256 /fd sha256 /tr http://timestamp.digicert.com /n "Publisher Name"'
```

## Publish Assets

Upload these files from the runtime-specific Velopack output directory to the same release feed location:

- `SIkebe.RepoSyncRadar-*-Setup.exe`
- `SIkebe.RepoSyncRadar-*-full.nupkg`
- `SIkebe.RepoSyncRadar-*-delta.nupkg`, when present
- `releases.<channel>.json`
- `assets.<channel>.json`

Keep the JSON manifests consistent with the `.nupkg` files that are actually available. Installed apps use those files to discover updates.

## Immutable GitHub Release Policy

RepoSyncRadar follows GitHub's immutable release guidance. GitHub Docs state that, once an immutable release is published, the release assets and associated Git tag cannot be changed: the tag cannot be moved or deleted while the release exists, assets cannot be modified or deleted, and the same tag name cannot be reused after deleting an immutable release. See [Immutable releases](https://docs.github.com/en/code-security/supply-chain-security/understanding-your-software-supply-chain/immutable-releases) and [Managing releases in a repository](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

The release workflow never replaces assets on an existing release and does not use `gh release upload --clobber`.

The workflow creates or reuses only an empty draft release, uploads the complete Velopack asset set once, and then either leaves the release as a draft or publishes it by clearing the draft flag. This keeps the installer, `.nupkg` packages, `releases.<channel>.json`, and `assets.<channel>.json` from being split across multiple upload attempts after publication.

Existing release behavior is intentionally strict:

- If the tag has no GitHub Release, the workflow creates a draft release and uploads assets.
- If the tag has an empty draft release, the workflow may reuse it.
- If the tag has a published release, the workflow fails. Create a new `RepoSyncRadarVersion` and tag for corrected assets. Do not delete the published release with the expectation that the same tag can be reused.
- If the tag has a draft release with assets and `draft` is disabled, the workflow validates that the attached asset names match the expected Velopack asset set and publishes the existing draft without replacing assets.
- If the tag has a draft release with assets and `draft` is enabled, the workflow fails. Delete the draft before rerunning if those assets were never published, or rerun with `draft` disabled after smoke validation to publish the existing draft.

For rollback, publish a newer corrective version rather than mutating the broken release. For example, if `v0.2.0` is published with bad assets, leave the release as historical record, fix the issue, set `RepoSyncRadarVersion` to `0.2.1`, tag `v0.2.1`, and publish a complete new Velopack feed for the affected channels. Installed clients should move forward to the corrected version through the update feed.

## GitHub Actions Release

The PR `CI` workflow runs the normal build/test gate and also builds the `win-x64` Velopack package, installs it on the runner, and runs the WebView E2E smoke against the installed `current\RepoSyncRadar.exe`. This keeps the installed-user-path validation before merge instead of waiting for a manual release from `main`.

The `Release` workflow builds, tests, packages `win-x64` and `win-arm64`, installs the generated `win-x64` package on the runner, runs the same installed WebView E2E smoke, and uploads the Velopack assets to a GitHub Release. The `win-arm64` package is built but not executed on GitHub-hosted x64 runners.

Trigger it manually from GitHub Actions with:

- `channelSuffix`: `stable`, `beta`, or `preview`; this produces channels such as `win-x64-stable`.
- `draft`: keep enabled for the dry-run smoke path. The workflow uploads all assets to a draft release and stops before publication, so maintainers can inspect the GitHub Release, download the installer, and run final checks. Rerun the workflow with `draft` disabled when you are ready to publish that existing draft; the workflow validates the attached asset names and publishes without re-uploading or replacing assets.
- `prerelease`: enable for beta/preview builds.

The workflow reads the release version from `RepoSyncRadarVersion` in `Directory.Build.props`; update that file before running a release. The workflow is intentionally manual-only, so pushing a tag or manually publishing an existing GitHub Release does not start another release build.

The workflow currently creates unsigned release assets. For public releases, select an Authenticode-compatible signing provider and extend the workflow once that provider is available.

## App Update Settings

Velopack startup hooks are installed in the app entry point, and the app can check/download updates in the background on startup. Updates are opt-in until the public update feed is finalized. Configure `appsettings.local.json` or release defaults with:

```json
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

When an update is found, the app downloads it and lets Velopack apply it on the next launch. It does not force-restart the user's running session.

Unsigned draft releases are acceptable for update-flow smoke tests. Broad public distribution remains blocked on selecting a code-signing path.

## Smoke Test

Before publishing a release broadly:

1. Run `dotnet build RepoSyncRadar.sln -warnaserror`.
2. Run `dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual`.
3. Build the Velopack installer for each target runtime.
4. Install on a clean Windows test profile.
5. Launch RepoSyncRadar and complete OAuth Device Flow.
6. Confirm the app can sync, open docs pages in WebView2, and run a Copilot-backed triage path.
7. Install the next version from the same channel and confirm update behavior.
