# RepoSyncRadar

A Windows desktop app that watches Repo sync PRs from [`github/docs`](https://github.com/github/docs) on a daily cadence and reduces the burden of broadcasting an unofficial changelog to social media, internal teams, and external audiences.

> [!IMPORTANT]
> This repository is a scaffold for a personal-use tool. See [`docs/DESIGN.md`](docs/DESIGN.md) for detailed design decisions, history, and the roadmap.

## Highlights

- **C# / .NET 8+ / WPF + BlazorWebView** — fast startup as a native Windows app
- Driven by the **GitHub Copilot SDK** ([`github/copilot-sdk`](https://github.com/github/copilot-sdk)) at its core
- Stores Focus / Hold / Rejected / Archive / Ignore / Boost data with **SQLite + EF Core**
- Hits **`docs.github.com/api/*`** directly to surface the actual rendered look and the file-path → public-URL mapping
- **No submodules.** The app is a standalone repository; no local clone is needed until Phase 6.

## Quick start

> [!NOTE]
> Requires the .NET 8 SDK or later and Windows 11. The Copilot CLI (bundled with the SDK) is downloaded on first run.

```powershell
git clone <this-repo-url> C:\github\RepoSyncRadar
cd C:\github\RepoSyncRadar
dotnet restore
dotnet build
dotnet run --project src/RepoSyncRadar.App
```

## Project layout

```
src/
├─ RepoSyncRadar.App/    ← WPF + BlazorWebView startup assembly
└─ RepoSyncRadar.Core/   ← Models / DbContext / options / service interfaces
docs/
└─ DESIGN.md             ← Design document (required reading)
```

## Roadmap

| Phase | Scope |
|---|---|
| 0 | Scaffold + design document (the current state of this repository) |
| 1 | Repo sync PR ingestion / commit display / official page embedding |
| 2 | Copilot SDK integration / Morning Triage session |
| 3 | Operational UI for Focus / Hold / Rejected / Archive / Ignore |
| 4 | Channel-specific drafts (Twitter / Teams / external) |
| 5 | Natural-language filtering (Ask Palette) |
| 6 | Local preview (bare clone + worktree) |
| 7 | Distribution and auto-update |

See [`docs/DESIGN.md`](docs/DESIGN.md#16-phase-別ロードマップ) for details.

## License

[MIT License](LICENSE)
