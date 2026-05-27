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
./scripts/Build-VelopackRelease.ps1 -Version 0.1.0 -Runtime win-x64
```

For Windows on Arm:

```powershell
./scripts/Build-VelopackRelease.ps1 -Version 0.1.0 -Runtime win-arm64
```

The script publishes the app and writes Velopack assets under `artifacts/release/velopack/<runtime>/`. The user-facing installer is `RepoSyncRadar-<channel>-Setup.exe`. The `.nupkg` files plus `releases.<channel>.json` form the update feed.

The script restores and uses the repo-local .NET tool manifest (`dotnet tool restore` / `dotnet tool run vpk`), so use `.config/dotnet-tools.json` to update the Velopack CLI version.

When rebuilding the same version/channel locally, pass `-Force` to clear the previous local Velopack output first:

```powershell
./scripts/Build-VelopackRelease.ps1 -Version 0.1.0 -Runtime win-arm64 -Force
```

## Signing

For local unsigned smoke tests, omit signing parameters. Public distribution must sign the app and installer artifacts. Azure Artifact Signing Basic was tested, but Public Trust identity validation is unavailable for the current Japan sold-to billing account, so do not use it as the default path.

With an existing `signtool.exe` parameter set:

```powershell
./scripts/Build-VelopackRelease.ps1 `
  -Version 0.1.0 `
  -Runtime win-x64 `
  -SignParams '/td sha256 /fd sha256 /tr http://timestamp.digicert.com /n "Publisher Name"'
```

## Publish Assets

Upload these files from the runtime-specific Velopack output directory to the same release feed location:

- `RepoSyncRadar-*-Setup.exe`
- `RepoSyncRadar-*-full.nupkg`
- `RepoSyncRadar-*-delta.nupkg`, when present
- `releases.<channel>.json`

Keep `releases.<channel>.json` consistent with the `.nupkg` files that are actually available. Installed apps use that file to discover updates.

## GitHub Actions Release

The `Release` workflow builds, tests, packages `win-x64` and `win-arm64`, and uploads the Velopack assets to a GitHub Release.

Trigger it manually from GitHub Actions with:

- `version`: SemVer version such as `0.1.0`.
- `channelSuffix`: `stable`, `beta`, or `preview`; this produces channels such as `win-x64-stable`.
- `draft`: keep enabled until the release has been installed and smoke-tested.
- `prerelease`: enable for beta/preview builds.

Pushing a tag like `v0.1.0` also runs the workflow. Tag-triggered releases are created as drafts by default.

The workflow currently creates unsigned draft release assets. For public releases, select an Authenticode-compatible signing provider and extend the workflow once that provider is available.

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