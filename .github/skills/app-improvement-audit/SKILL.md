---
name: app-improvement-audit
description: 'Hands-on audit of RepoSyncRadar as an application. USE FOR: アプリとしての改善点を徹底調査、実際に操作しながら判断、UX/起動/設定/エラー/性能/導線/アクセシビリティ/信頼性の改善探索、manual smoke, dogfooding, end-to-end app review. Runs the app, observes failures and UI behavior, inspects code/tests only after reproducing or mapping flows, ranks findings, implements safe scoped fixes, and validates with build/tests.'
argument-hint: '(任意) 重点領域: startup / settings / triage / drafts / preview / Copilot / UX / performance / report-only など'
---

# App Improvement Audit

## 役割

RepoSyncRadar を「コードの集合」ではなく「実際に使うアプリ」として監査する。起動し、操作し、画面・ログ・エラー・待ち時間・導線を観察して、ユーザー体験と信頼性の改善点を見つける。高確度で小さく直せるものは実装し、ビルドとテストで確認する。

## いつ使うか

- 「アプリとしての改善点がないか徹底的に調査して」と言われたとき
- 「実際に操作しながら判断して」と言われたとき
- 起動失敗、設定画面、Copilot 操作、Drafts、Preview、Morning Triage などの体験を dogfooding したいとき
- UI の分かりにくさ、エラー表示、進捗表示、アクセシビリティ、性能、状態遷移を見直したいとき
- リリース前の手動スモーク + 改善点洗い出しをしたいとき

## 絶対ルール

1. **まず実際に動かす**。`dotnet run --project src/RepoSyncRadar.App` などで起動を試し、失敗したらその失敗自体を最優先の finding として調査する。
2. **観察してからコードを読む**。画面、ログ、terminal output、テスト/診断結果を見て仮説を立て、その後に該当コードを読む。
3. **ユーザー体験で判断する**。内部実装の好みではなく、ユーザーが迷う・待つ・失敗に気づけない・復旧できない箇所を優先する。
4. **安全な修正だけ実装する**。原因が明確で範囲が小さい改善は実装する。大きな設計変更や挙動変更は report-only として提案する。
5. **既存変更を壊さない**。dirty worktree を前提に、無関係な変更は戻さない。
6. **機密情報を扱わない**。トークン、OAuth code、Copilot prompt/response content、telemetry content を不用意に表示・保存しない。
7. **操作不能を放置しない**。アプリが起動しない、操作に必要な前提が欠ける、認証が必要で進めない場合は、代替観察方法と残リスクを明示する。

## 手順

### 1. 重点領域を決める

ユーザーの引数や会話から、重点領域を選ぶ。

- `startup`: 起動、設定読み込み、DB migration、プロセス停止、初回体験
- `settings`: `appsettings.local.json`、設定 UI、保存/検証/再起動反映
- `triage`: Sync、Morning Triage、進捗、失敗/キャンセル、Copilot usage 表示
- `drafts`: Draft generation、JSON/非 JSON 応答、修復/再生成、エラー表示
- `preview`: docs preview、WebView2 navigation、Liquid rendering、ローカル preview server
- `Copilot`: 認証、SDK session、usage/AI Credits、permission、tool audit
- `UX`: 文言、導線、状態表示、空状態、アクセシビリティ、レスポンシブ/レイアウト
- `performance`: 起動時間、待ち時間、重い処理、キャッシュ、不要な再描画

指定がない場合は `startup` → `settings` → `triage/drafts` → `preview` → `Copilot` → `UX` の順で薄く広く見る。

### 2. 事前状態を記録する

1. `git status --short` で既存の未コミット変更を把握する。
2. 直近の terminal に `dotnet run --project src/RepoSyncRadar.App` の失敗がある場合は、その output を確認する。
3. `appsettings.json` / `appsettings.local.json` / 環境変数に依存する操作は、機密情報を表示せずに有無だけ確認する。
4. 既存の E2E / component tests が何をカバーしているか軽く見る。

### 3. 実際に起動して観察する

1. アプリを起動する。

   ```powershell
   dotnet run --project src/RepoSyncRadar.App
   ```

2. 起動に失敗したら、まず terminal output、例外、設定 validation、port/process lock、DB path、WebView2 のいずれかを切り分ける。
3. 起動できたら、主要画面を実際に操作する。
   - 初期画面の状態、空状態、次に何をすべきか
   - Settings の表示/編集/保存/エラー
   - Sync / triage / drafts / preview のボタン状態、進捗、キャンセル、失敗表示
   - Copilot usage / AI Credits 表示の分かりやすさ
   - WebView2 / preview の navigation、loading、リンク、テーマ
4. ブラウザ/CDP/Playwright 操作が使える場合は、スクリーンショットや DOM state で確認する。使えない場合は terminal output、E2E tests、component tests、コード上の state transition で代替する。

### 4. 観察メモを finding に変換する

各 finding は次の形式で整理する。

```text
[Priority] Title
Observed: 実際の操作・ログ・画面で見えた事実
Expected: アプリとして望ましい体験
Likely cause: 読んだコード上の原因
User impact: ユーザーがどう困るか
Action: implement / report-only / defer
Validation: 追加・更新するテストと実行コマンド
```

優先度の目安:

- P0: 起動不能、データ損失、認証不能、主要操作不能
- P1: 失敗しても理由や復旧手段が分からない、長時間待ちで不安、誤った情報を表示
- P2: 操作が遠い、文言が曖昧、空状態/進捗/キャンセルが弱い、設定が反映されにくい
- P3: 見た目の磨き込み、将来の改善、nice-to-have

### 5. コードとテストで裏を取る

観察した flow に対応するコードを読む。

- WPF shell / WebView2: `src/RepoSyncRadar.App/MainWindow.xaml.cs`
- Blazor UI: `src/RepoSyncRadar.App/Components/`
- Copilot orchestration: `src/RepoSyncRadar.App/Copilot/`
- Settings: `src/RepoSyncRadar.App/Settings/`、`src/RepoSyncRadar.Core/Options/`
- Preview: `src/RepoSyncRadar.Core/Services/Preview/`
- Data/service layer: `src/RepoSyncRadar.Core/Services/`、`src/RepoSyncRadar.Core/Data/`
- Tests: `tests/RepoSyncRadar.App.Tests/`、`tests/RepoSyncRadar.Core.Tests/`、`tests/RepoSyncRadar.App.E2E.Tests/`

観察とコードが一致しない場合は、仮説を修正してから実装に入る。

### 6. 安全な改善は実装する

実装してよい条件:

- 起動失敗や UX 問題を実際に観察している、または既存テストで再現できる
- 原因が特定できている
- 変更範囲が局所的
- 既存 UI/design pattern に沿っている
- 自動テストまたは明確な手動確認で検証できる

よくある改善パターン:

- 例外を握りつぶさず、ユーザー向けの復旧可能なエラーに変換する
- 進捗/キャンセル/再試行/空状態を既存 UI の文脈に合わせる
- 設定項目を options、local settings store、settings UI、docs、tests まで一貫して配線する
- 起動時 validation の不足を補う
- WebView2/preview の navigation allow-list、loading、fallback を明確にする
- Copilot 応答の parse failure に対して extraction / repair / fallback / friendly error を用意する

慎重に扱う候補:

- 認証スコープ変更や token storage 変更
- DB schema / migration の変更
- 大規模な UI 再設計
- preview server lifecycle の大改修
- SDK preview API への強依存

### 7. 検証する

狭い変更なら対象テストを先に実行する。

```powershell
dotnet test RepoSyncRadar.sln -- --filter-class <TestClassName>
```

完了前の標準検証:

```powershell
dotnet build RepoSyncRadar.sln -warnaserror
dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual
```

アプリ操作が主題の場合は、可能な範囲で再度起動し、修正した flow を操作する。認証や外部サービスが必要で完全再現できない場合は、どこまで確認できたかを明示する。

## 完了レポート

最後に日本語で短くまとめる。

- 実際に操作した範囲と観察結果
- 見つけた改善点の上位 findings
- 実装した修正と変更ファイル
- 実装しなかった改善候補と理由
- 検証コマンド、テスト結果、手動確認結果
- 残るリスクや次に見るべき flow

## 参考ファイル

- App shell: [`src/RepoSyncRadar.App/MainWindow.xaml.cs`](../../../src/RepoSyncRadar.App/MainWindow.xaml.cs)
- Blazor components: [`src/RepoSyncRadar.App/Components/`](../../../src/RepoSyncRadar.App/Components/)
- Copilot flows: [`src/RepoSyncRadar.App/Copilot/`](../../../src/RepoSyncRadar.App/Copilot/)
- Settings: [`src/RepoSyncRadar.App/Settings/`](../../../src/RepoSyncRadar.App/Settings/)
- Preview services: [`src/RepoSyncRadar.Core/Services/Preview/`](../../../src/RepoSyncRadar.Core/Services/Preview/)
- App tests: [`tests/RepoSyncRadar.App.Tests/`](../../../tests/RepoSyncRadar.App.Tests/)
- E2E tests: [`tests/RepoSyncRadar.App.E2E.Tests/`](../../../tests/RepoSyncRadar.App.E2E.Tests/)