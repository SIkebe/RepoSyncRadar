# RepoSyncRadar

[`github/docs`](https://github.com/github/docs) の Repo sync PR を日次で監視し、SNS / 社内 / お客様向けの「非公式 Changelog」発信負担を削減するための Windows デスクトップアプリ。

> [!IMPORTANT]
> 本リポジトリは個人運用ツールのスキャフォールドです。詳細な設計判断・経緯・ロードマップは [`docs/DESIGN.md`](docs/DESIGN.md) を参照してください。

## ハイライト

- **C# / .NET 8+ / WPF + BlazorWebView** — Windows ネイティブの軽快な起動
- **GitHub Copilot SDK** ([`github/copilot-sdk`](https://github.com/github/copilot-sdk)) を中核にしたエージェント駆動
- **SQLite + EF Core** で採用 / 却下 / Later / Ignore / Boost のデータを蓄積
- **`docs.github.com/api/*`** を直接叩いて、本物の見た目とファイルパス→公開 URL の対応を提示
- **submodule は使わない**。アプリは独立リポジトリ、ローカルクローンは Phase 6 まで不要

## クイックスタート

> [!NOTE]
> .NET 8 SDK 以降と Windows 11 が必要です。Copilot CLI(SDK にバンドル)を初回実行時にダウンロードします。

```powershell
git clone <this-repo-url> C:\github\RepoSyncRadar
cd C:\github\RepoSyncRadar
dotnet restore
dotnet build
dotnet run --project src/RepoSyncRadar.App
```

## プロジェクト構成

```
src/
├─ RepoSyncRadar.App/    ← WPF + BlazorWebView 起動アセンブリ
└─ RepoSyncRadar.Core/   ← モデル / DbContext / オプション / サービス IF
docs/
└─ DESIGN.md             ← 設計ドキュメント(必読)
```

## ロードマップ

| Phase | 内容 |
|---|---|
| 0 | スキャフォールド + 設計ドキュメント(本リポジトリの現状) |
| 1 | Repo sync PR 取得 / コミット表示 / 公式ページ埋め込み |
| 2 | Copilot SDK 統合 / Morning Triage セッション |
| 3 | Adopt / Reject / Later / Ignore の運用 UI |
| 4 | 媒体別下書き(Twitter / Slack / 顧客) |
| 5 | 自然言語フィルタ(Ask Palette) |
| 6 | ローカルプレビュー(bare clone + worktree) |
| 7 | 配布・自動更新 |

詳細は [`docs/DESIGN.md`](docs/DESIGN.md#16-phase-別ロードマップ) を参照。

## ライセンス

[MIT License](LICENSE)
