---
name: release-version-publish
description: 'Update RepoSyncRadarVersion and publish a new RepoSyncRadar release through the manual GitHub Actions Release workflow. USE FOR: バージョン更新、新しいリリース公開、Velopack release、GitHub Release 作成、draft release 作成、release workflow 実行、RepoSyncRadarVersion bump, publish release, ship new version. Bumps Directory.Build.props, validates build/tests and release preconditions, opens/updates a PR when needed, then runs the manual Release workflow first as a draft and publishes only after explicit confirmation.'
argument-hint: 'version and intent, e.g. 0.1.16 / 0.2.0-beta.1, draft only, publish after draft validation'
---

# Release Version And Publish (RepoSyncRadar)

## 役割

RepoSyncRadar の app/package version を更新し、Velopack installer/update-feed assets を GitHub Release として安全に公開する。`RepoSyncRadarVersion` の bump、通常 gate、PR、manual-only `Release` workflow の draft 実行、draft 検証、明示確認後の publish までを扱う。

## いつ使うか

- 「バージョンを `0.x.y` に上げて」「新しいリリースを作って」と言われたとき
- 「Release workflow を回して」「Velopack release を公開して」と言われたとき
- published release の修正が必要になり、immutable policy に従って新しい version/tag を切るとき
- draft GitHub Release を作成・検証・公開するとき

## 絶対ルール

1. **公開は不可逆として扱う**。published GitHub Release / tag / assets は変更しない。修正は必ず新しい `RepoSyncRadarVersion` と `v<version>` tag で行う。
2. **`RepoSyncRadarVersion` だけを正式 version source とする**。通常リリースでは個別 project の `<Version>` や workflow input に ad hoc version を入れない。
3. **Release workflow は draft で先に実行する**。`draft=true` で assets を生成・アップロードし、GitHub Release と installer/update feed を検証してから publish に進む。
4. **publish 前にユーザーの明示確認を取る**。「公開して」「publish して」などの明確な指示なしに `draft=false` の workflow 実行や draft の公開をしない。
5. **PR を勝手に merge しない**。CI が全て緑でも、`gh pr merge`、merge button 相当操作、auto-merge enable はユーザーが明示的に「merge して」と指示した場合だけ実行する。
6. **Release workflow を勝手に起動しない**。PR が merge 済みでも、ユーザーが明示的に「draft release workflow を実行して」と指示するまで `gh workflow run release.yml` を実行しない。
7. **official assets は installer/update-feed のみ**。Velopack build は `-NoPortable -NoLegacyManifest` を維持し、portable bundle や legacy Squirrel manifest を公開しない。
8. **installed package smoke を重視する**。Release workflow の `win-x64` installed smoke が赤なら公開しない。ローカル smoke が必要な場合も uninstaller 経由で cleanup する。
9. **既存 draft/published release を上書きしない**。draft に assets がある場合、既存 assets を公開するなら `draft=false`、作り直すなら新しい `RepoSyncRadarVersion` を使う。`draft=true` を再実行して assets を置換しない。
10. **未コミット変更を壊さない**。関係ない dirty files は戻さない。release version bump に必要な差分だけ触る。

## 手順

### 1. 作業状態と対象 version を確認する

1. `git status --short --branch` で branch と dirty files を確認する。
2. ユーザー指定 version を SemVer として確認する。
3. version 未指定なら、`gh release list --repo SIkebe/RepoSyncRadar --limit 20` と `gh release view` で現在公開されている最新 published release を調べる。draft / prerelease / local `RepoSyncRadarVersion` だけを根拠にしない。
4. latest published version と `Directory.Build.props` の current `RepoSyncRadarVersion` を比較し、新規公開すべき version を判断する。
   - current `RepoSyncRadarVersion` が latest published より大きく、同じ tag の published release がなければ、通常は current version を publish candidate にする。
   - current `RepoSyncRadarVersion` が latest published 以下なら、stable patch release では latest published の patch を 1 つ上げた version を候補にする。
   - ユーザーが prerelease intent を示した場合だけ、latest published / latest prerelease を分けて確認し、`0.x.y-beta.N` や preview channel を候補にする。
   - local current が latest published より 2 つ以上進んでいる、または published / draft / local の関係が不自然な場合は、推測で進めず候補と根拠を提示して確認する。
5. `Directory.Build.props` の `RepoSyncRadarVersion` を読み、target version を確定する。target が current 以下なら、意図的な prerelease / hotfix でない限り停止して確認する。
6. tag 名は必ず `v<RepoSyncRadarVersion>` にする。

確認例:

```powershell
git status --short --branch
Select-String -Path Directory.Build.props -Pattern 'RepoSyncRadarVersion'
gh release list --repo SIkebe/RepoSyncRadar --limit 20
gh release view v0.1.16 --repo SIkebe/RepoSyncRadar --json isDraft,assets
```

### 2. release preconditions を確認する

1. `gh release view v<version>` で既存 Release を確認する。
2. 結果別の対応:

| 状態 | 対応 |
|---|---|
| Release なし | 続行。workflow が draft release を作る |
| 空の draft release あり | 続行可 |
| assets 付き draft release あり | `draft=false` で既存 assets を publish する意図かユーザー確認。`draft=true` の再実行はしない |
| published release あり | 停止。新しい `RepoSyncRadarVersion` を選ぶ |

### 3. version bump を実装する

1. `Directory.Build.props` の `RepoSyncRadarVersion` を target version に更新する。
2. version に直接ひも付く docs がある場合だけ更新する。通常は release notes 本文は workflow が生成するため不要。
3. 変更を確認する。

```powershell
git diff -- Directory.Build.props
```

### 4. 通常 gate を通す

リリース version bump でも通常 gate を通す。

```powershell
dotnet build RepoSyncRadar.sln -warnaserror
dotnet test RepoSyncRadar.sln -- --filter-not-trait Category=Manual
```

release workflow / GitHub Actions を変更した場合は追加で:

```powershell
ghalint run
```

### 5. PR を作成または更新する

version bump を `main` に入れるため、PR を作るか既存 PR を更新する。

1. `.github/pull_request_template.md` を読む。
2. PR description は英語で、全 section を埋める。
3. `Validation` は実行済みなら check、未該当なら `N/A - <reason>` に置換する。
4. branch を push してから PR を作る。`create_pull_request` が 422 で失敗した場合は、未 push branch や既存 PR を疑い、`git push -u origin <branch>` と `gh pr list --head <branch>` を確認してから `gh pr create` で再試行する。
5. merge 前に CI が緑であることを確認する。
6. **ここで停止する**。CI が緑でも PR を merge しない。ユーザーに PR URL、CI 結果、次に必要な明示操作(`merge して` / `draft release workflow を実行して`)を報告する。

PR summary に最低限含める:

- `RepoSyncRadarVersion` old -> new
- release channel / prerelease intent
- validation commands
- publish は merge 後に manual `Release` workflow で行うこと

### 6. main 反映後に draft release workflow を実行する

ユーザーが明示的に draft release workflow の実行を指示した場合だけ進む。`main` に version bump が入ったことを確認してから、まず draft で実行する。PR を agent 自身が merge してこの step に進んではいけない。

```powershell
gh workflow run release.yml `
  --repo SIkebe/RepoSyncRadar `
  --ref main `
  -f channelSuffix=stable `
  -f draft=true `
  -f prerelease=false
```

prerelease version (`0.2.0-beta.1` など) では通常:

```powershell
gh workflow run release.yml `
  --repo SIkebe/RepoSyncRadar `
  --ref main `
  -f channelSuffix=preview `
  -f draft=true `
  -f prerelease=true
```

run を追跡する。長い `gh run watch` は出力が肥大化しやすいため、通常は JSON で状態を短く確認し、必要なときだけ watch する。

```powershell
gh run list --repo SIkebe/RepoSyncRadar --workflow Release --limit 5
gh run view <run-id> --repo SIkebe/RepoSyncRadar --json status,conclusion,url,jobs
```

失敗したら `gh run view <run-id> --log-failed` で原因を読み、version/tag/draft/assets の状態を壊さずに修正方針を提示する。

### 7. draft release を検証する

workflow 成功後、`v<version>` draft release を確認する。

```powershell
gh release view v0.1.16 `
  --repo SIkebe/RepoSyncRadar `
  --json isDraft,isPrerelease,tagName,targetCommitish,assets
```

確認項目:

- `isDraft` が `true`
- `isPrerelease` が意図通り
- tag が `v<RepoSyncRadarVersion>`
- `win-x64-stable` / `win-arm64-stable` など対象 channel の installer、`.nupkg`、`releases.<channel>.json`、`assets.<channel>.json` が揃っている
- unexpected asset や portable / legacy `RELEASES-*` がない
- Release workflow の build/test/package/installed smoke が緑
- 必要なら draft installer をダウンロードして手元で起動 smoke。cleanup は Velopack uninstaller を使う

### 8. publish 前の最終確認

publish は明示確認が必要。ユーザーに次を提示し、「公開して」と同等の明確な返答を待つ。

- version / tag
- channel suffix
- prerelease flag
- draft release URL
- assets count と主要 asset 名
- workflow run result
- immutable release policy: 公開後は assets/tag を差し替えず、修正は新 version で行う

### 9. draft release を公開する

ユーザー確認後、既存 draft assets を publish するため `draft=false` で同じ workflow を再実行する。workflow は attached asset names を検証し、再アップロードせずに draft を publish する。

```powershell
gh workflow run release.yml `
  --repo SIkebe/RepoSyncRadar `
  --ref main `
  -f channelSuffix=stable `
  -f draft=false `
  -f prerelease=false
```

run を追跡:

```powershell
gh run list --repo SIkebe/RepoSyncRadar --workflow Release --limit 5
gh run watch <run-id> --repo SIkebe/RepoSyncRadar --exit-status
```

完了後:

```powershell
gh release view v0.1.16 --repo SIkebe/RepoSyncRadar --json isDraft,isPrerelease,assets,url
```

`isDraft=false` であることを確認する。

## 失敗時の振る舞い

| 失敗種別 | 対応 |
|---|---|
| build/test が赤 | publish しない。原因を修正して PR/CI からやり直す |
| release preflight が published release を検出 | 新 version を選ぶ。既存 published release を削除・上書きしない |
| draft に partial/unexpected assets | 既存 asset set は公開・置換せず、新しい `RepoSyncRadarVersion` で完全な asset set を作る |
| 明示確認なしに PR merge / draft workflow 実行まで進めてしまった | そこで即停止。追加の publish / delete / rerun は行わず、merge commit、workflow run、draft release 状態、assets 数を報告してユーザー判断を待つ |
| package / installed smoke が赤 | publish しない。`gh run view --log-failed` と artifact を確認し、修正 PR を作る |
| publish workflow が asset validation で赤 | release 状態を変えずに停止し、draft asset set と expected names を報告 |
| 公開後に不具合発見 | release を mutate しない。hotfix version を bump して新 release を作る |

## やってはいけないこと

- `git push --force`、tag の移動、published release asset の削除・再アップロード
- `RepoSyncRadarVersion` 以外への恒久的な version source 追加
- official release で portable bundle / legacy Squirrel manifest を公開
- CI 緑を理由に PR を自動 merge すること
- PR merge 後に自動で Release workflow を起動すること
- `draft=false` をユーザー確認なしに実行
- release workflow を tag push trigger に変更
- installer cleanup で `%LOCALAPPDATA%\SIkebe.RepoSyncRadar` を直接削除して済ませる

## 完了レポート

最後に日本語で短くまとめる。

- bumped version と tag
- PR / merge 状態
- draft workflow run ID と結果
- publish workflow run ID と結果(実行した場合)
- GitHub Release の draft/prerelease 状態
- assets の概略
- 残っている手動確認や blocker

## 参考

- Version source: [`Directory.Build.props`](../../../Directory.Build.props)
- Release workflow: [`.github/workflows/release.yml`](../../../.github/workflows/release.yml)
- Packaging script: [`scripts/Build-VelopackRelease.ps1`](../../../scripts/Build-VelopackRelease.ps1)
- Installed smoke wrapper: [`scripts/Test-VelopackInstalledE2E.ps1`](../../../scripts/Test-VelopackInstalledE2E.ps1)
- Release docs: [`docs/RELEASE.md`](../../../docs/RELEASE.md)
- PR template: [`.github/pull_request_template.md`](../../../.github/pull_request_template.md)
