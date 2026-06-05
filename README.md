# RepoSyncRadar

RepoSyncRadar is a Windows desktop app for GitHub Enterprise Cloud administrators who need to watch [`github/docs`](https://github.com/github/docs) repo sync changes and understand whether they may affect the environments they manage. It helps spot product, policy, billing, security, and operational changes that can appear in documentation before, alongside, or without a dedicated GitHub Changelog post.

> [!IMPORTANT]
> This app is aimed at operator review and triage. It does not replace official GitHub release notes, GitHub Changelog posts, or support guidance; it helps administrators notice docs-driven signals that deserve review.

## Highlights

- **C# / .NET 10 / WPF + BlazorWebView** — fast startup as a native Windows app
- Driven by the **GitHub Copilot SDK** ([`github/copilot-sdk`](https://github.com/github/copilot-sdk)) at its core
- Reviews `github/docs` repo sync PRs for changes that may affect GitHub Enterprise Cloud administration and operations
- Surfaces docs previews and file-path → public-URL mapping so reviewers can inspect the rendered impact
- Generates operator-facing sharing drafts for Twitter and customer-facing notices
- Stores Focus / Hold / Rejected / Archive / Ignore / Boost data with **SQLite + EF Core**
- Uses GitHub OAuth Device Flow by default; the public OAuth Client ID is bundled, and organizations can override it with their own OAuth App if policy requires it
- **No submodules.** The app is a standalone repository; no local clone is needed for normal triage.

## Quick start

> [!NOTE]
> Requires Windows 11, the .NET SDK pinned in [`global.json`](global.json), and an active GitHub Copilot subscription. The Copilot CLI used by the SDK is downloaded on first run.

```powershell
git clone <this-repo-url> C:\github\RepoSyncRadar
cd C:\github\RepoSyncRadar
dotnet restore
dotnet build
dotnet run --project src/RepoSyncRadar.App
```

On first launch, RepoSyncRadar signs in with GitHub OAuth Device Flow. Normal distribution builds include the public RepoSyncRadar OAuth Client ID, so users do not need to create their own OAuth App. Organizations that require a managed OAuth App can override `Copilot:OAuthClientId` in `src/RepoSyncRadar.App/appsettings.local.json` or with `RADAR_Copilot__OAuthClientId`.

For the public OAuth App description, use wording like:

> RepoSyncRadar helps GitHub Enterprise Cloud administrators review GitHub Docs updates for product, policy, and operational changes that may affect their managed environments.

Public-release blockers and follow-ups are tracked in [`docs/PUBLIC_RELEASE_READINESS.md`](docs/PUBLIC_RELEASE_READINESS.md).

## Project layout

```
src/
├─ RepoSyncRadar.App/    ← WPF + BlazorWebView startup assembly
└─ RepoSyncRadar.Core/   ← Models / DbContext / options / service interfaces
docs/
├─ DESIGN.md                    ← Product design and architecture notes
├─ PUBLIC_RELEASE_READINESS.md  ← Public release blockers and checklist
└─ USAGE.md                     ← Setup, OAuth, and day-to-day usage
```

## Roadmap

The original phase plan is tracked in [`docs/DESIGN.md`](docs/DESIGN.md) and the implementation checklist in [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md).

| Phase | Scope |
|---|---|
| 0 | Scaffold + design document |
| 1 | Repo sync PR ingestion / commit display / official page embedding |
| 2 | Copilot SDK integration / Morning Triage session |
| 3 | Operational UI for Focus / Hold / Rejected / Archive / Ignore |
| 4 | Channel-specific drafts (Twitter / customer-facing) |
| 5 | Local preview (bare clone + Markdown/Liquid renderer) |
| 6 | Distribution and auto-update |

See [`docs/DESIGN.md`](docs/DESIGN.md#16-phase-別ロードマップ) for details.

## License

[MIT License](LICENSE)
