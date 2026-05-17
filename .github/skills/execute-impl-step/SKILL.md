---
name: execute-impl-step
description: 'Execute the next pending step from docs/IMPLEMENTATION_PLAN.md in the RepoSyncRadar workspace. USE FOR: 実装プランの次のステップを進める、Step N を実装する、進捗を進める、IMPLEMENTATION_PLAN を 1 段階前進、次の TODO、execute next step, advance plan, implement next step, run next implementation step. Follows the strict test-first workflow defined in the plan: identifies the first unchecked step, implements its scope test-first, runs `dotnet build -warnaserror` + `dotnet test -- --filter-not-trait Category=Manual`, updates the progress checkbox only when green, and proposes a commit message for user approval. ALWAYS stops after one step — never auto-advances to the next.'
argument-hint: '(任意) Step 番号を指定。省略時は最初の未完了ステップ。例: 3, Step 5'
---

# Execute Implementation Step (RepoSyncRadar)

## 役割

[`docs/IMPLEMENTATION_PLAN.md`](../../../docs/IMPLEMENTATION_PLAN.md) に記載された実装ステップを **1 回の呼び出しで 1 ステップだけ** 実行する。テストが緑になることを完了の唯一の判定にし、緑になるまで次のステップへは絶対に進まない。

## いつ使うか

- 「次のステップを実装して」「Step 3 を進めて」と言われたとき
- 朝の開発開始時に最初の未完了ステップを片付けたいとき
- レビュー後、修正が終わってから当該ステップを締めるとき

## 絶対ルール

1. **1 回の呼び出しで 1 ステップだけ進める**。複数ステップを連結しない。
2. **テストが緑** で初めて当該ステップの完了とする。`dotnet build -warnaserror` と `dotnet test -- --filter-not-trait Category=Manual` の両方をローカルで通すこと。
3. **進捗チェックボックスを書き換えるのはテスト緑後のみ**。失敗時は触らない。
4. **コミットは提案のみ**。ユーザーが「コミットして」と明言したときだけ `git commit` を実行する。`git push` は絶対にしない。
5. **`Category=Manual` のテストは合格条件に含めない**。完了基準内の「手動スモーク」は完了基準とは別物として、最後にチェックリストとして提示する。
6. **設計の変更が必要になったら立ち止まる**。プランから逸脱せず、ユーザーに相談する(plan 自体の修正も提案ベース)。

## 手順

### 1. ステップを特定する

1. [`docs/IMPLEMENTATION_PLAN.md`](../../../docs/IMPLEMENTATION_PLAN.md) を読む。
2. 末尾の「進捗」セクションで **最初の `- [ ] Step N`** を見つける。ユーザーが引数で番号(`3` / `Step 5` 等)を指定していればそれを優先。
3. その Step の本文(目的 / スコープ / テスト / 完了基準)を **そのセクション全体** 読み込む。
4. 必要なら参照される [`docs/DESIGN.md`](../../../docs/DESIGN.md) の対応セクションも読む。

### 2. 着手前にプランをユーザーに合意してもらう

短く以下を提示する:

- 対象ステップ番号と目的
- 追加/変更する主要ファイル(プランの「スコープ」を箇条書きで)
- 追加するテストクラス名と件数(プランの「テスト」表から)
- 完了判定に走らせるコマンド

ユーザーが「OK」と言ったら次へ。質問やプラン修正要望が来たら **そこで止まる**。

### 3. テスト先行で実装する

1. 当該ステップが想定するテストプロジェクトが無ければ作る。命名・配置は付録 A / B を厳守。
2. プランの「テスト」表に列挙された **全ケースを失敗するテスト** として先に書く。空実装の throw でビルドは通しておく。
3. プロダクションコードを書き、テストを 1 件ずつ緑にしていく。
4. 既存テストが壊れていないかを常に `dotnet test` で確認。

### 4. 完了判定コマンドを走らせる

```powershell
dotnet build -warnaserror
dotnet test --no-build -- --filter-not-trait Category=Manual
```

両方が **完全に緑** であること。警告 1 つでも残せば失敗扱い。

失敗時:

- 原因を特定して **同じステップ内で** 直す。次ステップを先取りしない。
- 設計の見直しが必要そうなら、進捗チェックは触らずに **ユーザーへ報告して停止**。

### 5. 進捗を更新する

緑が確定したら [`docs/IMPLEMENTATION_PLAN.md`](../../../docs/IMPLEMENTATION_PLAN.md) 末尾の進捗リストを編集する:

- 対象行 `- [ ] Step N — <タイトル>` → `- [x] Step N — <タイトル>`
- その下に追記:

  ```text
  - [x] Step N — 完了日 YYYY-MM-DD, テスト件数 NN
  ```

  日付は `Get-Date -Format yyyy-MM-dd` で取得した実日付を使う。コミット SHA は記録しない(`--amend` で自己参照不能になるため。SHA は `git log` 側で追跡)。

### 6. コミット案を提示する(実行しない)

`git status --short` で変更ファイルを確認し、以下のフォーマットで案を提示:

```text
Suggested commit message:
  Step N: <ステップタイトル> を実装

  - 追加: <主なファイル>
  - テスト: <件数> 件追加 / 全 <件数> 件緑
  - 参照: docs/IMPLEMENTATION_PLAN.md §Step N

Files to stage:
  src/...
  tests/...
  docs/IMPLEMENTATION_PLAN.md
```

ユーザーが「コミットして」と明言したら `git add` + `git commit` を実行。それ以外なら **絶対に実行しない**。

### 7. 完了レポートを出す

最後に以下を 1 ブロックで報告:

- 完了した Step 番号
- 追加/変更したファイル一覧(`git diff --name-status`)
- テスト結果サマリ(件数、所要時間)
- **次ステップの番号と一行サマリ**(プレビュー — 着手はしない)
- 手動スモークが残っている場合はチェックリスト形式で列挙

## 失敗時の振る舞い

| 失敗種別 | 対応 |
|---|---|
| `dotnet build` が警告/エラーで赤 | 進捗を更新せず、原因を特定して同ステップ内で修正 |
| `dotnet test` が一部赤 | 同上。テストの期待値が間違っていれば直すが、プランの意図とずれる修正は **必ずユーザーに確認** |
| プランのテストケースに不足を発見 | 進捗を更新する前にプランの該当 Step の表に追記してから完了 |
| プランそのものが現実と乖離 | 進捗を更新せず、`docs/IMPLEMENTATION_PLAN.md` の修正案 + 理由を提示して停止 |
| 範囲が想定より大きい(>2h 規模) | 当該 Step を分割する案を提示して停止(勝手に進めない) |

## やってはいけないこと

- **複数ステップの連続実行**: 「ついでに次もやる」「依存を先取り」は禁止。
- **進捗チェックボックスの先回り更新**: テスト緑前の更新は厳禁。
- **`Category=Manual` テストや手動チェックの自動化**: 完了基準から除外、別途リストアップ。
- **`appsettings.Local.json` のコミット / 認証トークン類の埋め込み**: 機微情報はリポジトリに入れない。
- **`docs/DESIGN.md` の設計変更**: 必要時はプラン側を直すか、設計変更提案として **別途** 起こす。
- **`git push` / `git reset --hard` / `git rebase` などの破壊的操作**: 必ずユーザー確認。

## 参考

- 実装プラン本体: [`docs/IMPLEMENTATION_PLAN.md`](../../../docs/IMPLEMENTATION_PLAN.md)
- 設計ドキュメント: [`docs/DESIGN.md`](../../../docs/DESIGN.md)
- リポジトリのコーディング規約: [`.editorconfig`](../../../.editorconfig)
- 改行コード規約: [`.gitattributes`](../../../.gitattributes)(LF 統一)
