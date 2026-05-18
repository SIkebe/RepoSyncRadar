---
name: update-copilot-pricing-table
description: 'Update RepoSyncRadar Copilot model pricing fallback from GitHub Docs. USE FOR: GitHub Copilot models-and-pricing 更新、AI Credits 価格表更新、CopilotModelPricing の更新、usage-based billing model pricing refresh, official pricing table sync. Reads the official GitHub Docs pricing page and raw docs data, updates the in-app `CopilotModelPricing` table, adjusts tests/docs, and validates with focused usage tests plus build/full automated tests.'
argument-hint: '(任意) 価格表 URL または raw YAML URL。省略時は GitHub Docs Models and pricing for GitHub Copilot。'
---

# Update Copilot Pricing Table

## 役割

RepoSyncRadar が SDK から AI Credits / nano AIU を受け取れない場合に使う、アプリ内の GitHub Copilot モデル別価格 fallback を、公式 GitHub Docs の価格表に合わせて更新する。価格を推測せず、公式ページまたは `github/docs` の raw data を根拠に `src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs` 内の `CopilotModelPricing` を更新し、usage 推定テストと UI/docs の整合性まで確認する。

## いつ使うか

- 「Copilot のモデル別価格表を更新して」と言われたとき
- `https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing` の内容が変わった可能性があるとき
- 新しい model ID / model name が Copilot SDK から返り、`credits 未報告` になるとき
- usage-based billing / AI Credits fallback の表示や計算が古い疑いがあるとき

## 公式情報源

既定では以下を確認する。

1. 公開ページ: `https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing`
2. raw data: `https://raw.githubusercontent.com/github/docs/main/data/tables/copilot/models-and-pricing.yml`
3. 関連概念: `https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-organizations-and-enterprises`

公開ページは生成後の表確認に使い、raw YAML は表の完全性確認に使う。raw YAML が取得できない場合でも公開ページから更新できるが、その場合は最終レポートで「raw data 未確認」と明記する。

## 絶対ルール

1. **公式価格だけを入れる**。ブログ、推測、SDK の不完全な表示、スクリーンショット由来の価格を固定テーブルに入れない。
2. **単位を間違えない**。GitHub Docs の価格は USD per 1M tokens。アプリでは `EstimateAiCredits(tokens, usdPerMillionTokens)` が `tokens * usdPerMillionTokens / 10_000` で AI Credits に変換する。`1 AI Credit = $0.01 USD`、`1_000_000_000 nano AIU = 1 AI Credit`。
3. **Anthropic cache write を別扱いする**。Anthropic 表には cache write cost がある。`ModelTokenPricing(input, cachedInput, output, cacheWrite)` の第 4 引数に入れる。OpenAI/Google/Fine-tuned など cache write 列が無いモデルには入れない。
4. **不明モデルを推測しない**。公式価格表にないモデルは `SupportsModel` が false のままにし、UI は `credits 未報告` を維持する。
5. **正規化と別名をテストする**。SDK が `gpt-5.5`、`claude-sonnet-4.5` のような ID 形式を返しても、公式表の表示名と一致するよう `NormalizeModelKey` の既存挙動を確認する。
6. **dirty worktree を尊重する**。関係ない未コミット変更は戻さない。価格表更新に必要なファイルだけを触る。

## 手順

### 1. 現状を確認する

1. `git status --short` で未コミット変更を確認する。
2. `src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs` の `CopilotModelPricing` を読む。
3. 関連テストを読む。
   - `tests/RepoSyncRadar.App.Tests/Copilot/CopilotUsageTrackerTests.cs`
   - 必要に応じて `tests/RepoSyncRadar.App.Tests/Components/AppHeaderTests.cs`
4. `docs/USAGE.md` の Copilot 使用量説明を確認する。

### 2. 公式価格表を取得する

1. ユーザー指定 URL があれば最初に読む。指定がなければ既定の Models and pricing ページを読む。
2. raw YAML `data/tables/copilot/models-and-pricing.yml` を取得して、provider ごとの model、status、input、cached input、output、cache write を確認する。
3. 公開ページと raw YAML に差異があれば、raw YAML の構造と公開ページの表示を比較し、どちらを採用したかを記録する。
4. footnote は価格の採用可否に影響する場合だけコードや docs に反映する。例: long-context 条件、included model、他モデル価格を使う fine-tuned model。

### 3. アプリ内テーブルを更新する

`CopilotUsageTracker.cs` の `CopilotModelPricing._pricing` を更新する。

- OpenAI / Anthropic / Google / xAI / Fine-tuned など provider ごとに、公式表と同じ model name を `NormalizeModelKey("...")` へ渡す。
- `new(inputUsdPerMillion, cachedInputUsdPerMillion, outputUsdPerMillion)` を基本形にする。
- cache write がある場合だけ `new(input, cachedInput, output, cacheWrite)` にする。
- 価格の小数は公式表に合わせ、意味のない丸めや変換済み AI Credits 値を入れない。
- 旧モデルが公式表から消えた場合は、ユーザーへの影響を考えて扱いを決める。原則として公式表に無いモデルは固定テーブルから外すが、SDK がまだ返す known model なら最終レポートで確認事項として明記する。

### 4. テストを更新する

最低限、以下を確認する。

- OpenAI 系モデルの input / cached input / output / reasoning token が公式価格から AI Credits に変換される。
- Anthropic 系モデルの cache write が加算される。
- cache write 列が無いモデルでは cache write tokens が加算されない。
- 公式表にない unknown model は推定しない。
- SDK session metrics で `CurrentModel` だけがある場合も fallback 推定される。
- 新規/変更モデル名について、SDK ID 形式 (`gpt-5.5`, `claude-sonnet-4.5`, `gemini-3.1-pro` など) が正規化で当たる。

関連テストは主に `CopilotUsageTrackerTests`。UI の表示文言や billing source 表示に影響する場合は `AppHeaderTests` も更新する。

### 5. docs と instructions を必要に応じて更新する

- `docs/USAGE.md` の説明が古くなった場合は更新する。
- 今回の作業で今後も有効な手順や落とし穴を見つけた場合は、`.github/copilot-instructions.md` の既存 bullet を短く更新する。単発の価格値変更だけなら instructions へは追加しない。

### 6. 検証する

まず価格表に関係する focused tests を実行する。

```powershell
dotnet test tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj -- --filter-class RepoSyncRadar.App.Tests.Copilot.CopilotUsageTrackerTests
```

UI 表示も触った場合は追加で実行する。

```powershell
dotnet test tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj -- --filter-class RepoSyncRadar.App.Tests.Components.AppHeaderTests
```

完了前に標準 gate を通す。

```powershell
dotnet build RepoSyncRadar.sln -warnaserror
dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual
```

テストログが古い selector や古いバイナリを参照しているように見える場合は、App.Tests を clean してから focused test を再実行する。

```powershell
dotnet clean tests/RepoSyncRadar.App.Tests/RepoSyncRadar.App.Tests.csproj
```

## 完了レポート

最後に日本語で短く報告する。

- 参照した公式 URL と raw data URL
- 追加・変更・削除したモデル数と代表例
- cache write の扱い、unknown model の扱い、単位変換の確認結果
- 変更ファイル
- 実行した検証コマンドと結果
- 残した確認事項があればそれ

## 参考ファイル

- Pricing fallback: [`src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs`](../../../src/RepoSyncRadar.App/Copilot/CopilotUsageTracker.cs)
- Usage tracker tests: [`tests/RepoSyncRadar.App.Tests/Copilot/CopilotUsageTrackerTests.cs`](../../../tests/RepoSyncRadar.App.Tests/Copilot/CopilotUsageTrackerTests.cs)
- Usage UI tests: [`tests/RepoSyncRadar.App.Tests/Components/AppHeaderTests.cs`](../../../tests/RepoSyncRadar.App.Tests/Components/AppHeaderTests.cs)
- Usage docs: [`docs/USAGE.md`](../../../docs/USAGE.md)