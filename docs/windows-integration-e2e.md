# Windows 統合 E2E 設計

## 目的

Squirrel Notifier の単体テストでは検出できない、Windows 配布物、外部 CLI、
mcp-gateway、thread-owl、認証、通知購読、WinUI の組み合わせによる回帰を継続的に
検出する。

統合 E2E は、実行環境と決定性が異なる次の 2 段階へ分離する。

| Phase | 用途 | Runner | 実行契機 | PR gate |
|---|---|---|---|---|
| Phase 1 | 決定的な headless 統合 E2E | GitHub-hosted `windows-2025` | pull request / `workflow_dispatch` | 安定化後に required |
| Phase 2 | 実デスクトップを含むシステム E2E | snapshot 復元可能な専用 Windows VM | nightly / `workflow_dispatch` / release 前 | required にしない |

Phase 1 は外部ネットワークや本番レビュー基盤に依存させない。Phase 2 は実際の WinUI、
既定ブラウザ、固定バージョンの test stack を扱うが、常用開発 PC や本番資格情報は
使用しない。

本書は Issue #184 の全体設計を定義する。個別実装は #220〜#224 で追跡する。

## 設計原則

1. Squirrel Notifier は consumer-side acceptance のみを検証する。
2. OAuth、MCP transport、review queue をテスト用に再実装しない。
3. Phase 1 は同じ入力に対して同じ結果を返すローカル fixture を使用する。
4. Phase 2 は VM snapshot から毎回クリーンな状態を復元する。
5. 失敗は runner / test harness / dependency contract / product のどこで起きたかを
   構造化して記録する。
6. retry や `continue-on-error` で flaky test を隠さない。
7. CI の target SDK、runtime、toolchain、主要依存を高速化目的でダウングレードしない。
8. production の PR、queue、GitHub App、token へ書き込まない。

## 責務境界

| Component | 統合 E2E での責務 | 本リポジトリに持ち込まないもの |
|---|---|---|
| squirrel-notifier | 配布物の build / install / launch、設定、外部 CLI 起動、購読状態、通知モデル、launcher 引数、ログと artifact | OAuth、MCP transport、queue の代替実装 |
| Mcp-Docker | Phase 2 の固定 test stack、gateway route、container version / digest、health check、初期化と停止手順 | Squirrel Notifier 固有の UI assertion |
| thread-owl | webhook、review candidate、queue、MCP tool / resource の製品契約 | subscriber client、agent wait loop |
| mcp-resource-subscriber | `resources/subscribe`、通知待機、`resources/read`、CLI 出力と終了コードの製品契約 | Squirrel Notifier の状態管理 |
| mcp-gateway | routing、認証境界、代表的な HTTP / MCP error contract | review workflow の中央状態機械 |
| review-raven | reviewed-side の thread 返信・resolve 等の製品契約 | reviewer-side queue と通知 |

Phase 1 の fake endpoint と dummy process は、上記 component の内部実装を模倣しない。
Squirrel Notifier が受け取る公開済みの入力と終了状態だけを fixture として再生する。
component 自体の詳細な contract test は各 component repository が所有する。

## Phase 1: GitHub-hosted Windows headless E2E

### 対象

Phase 1 は `windows-2025` で実行し、対話デスクトップを必要としない次の境界を検証する。

- 実 publish payload、MSI、セットアップ Zip の生成
- MSI の silent install / uninstall、配置先、製品 version、後始末
- PATH / PATHEXT と `.exe` / `.cmd` / `.bat` のプロセス起動
- version pin した mcp-resource-subscriber の CLI 引数と出力契約
- ローカル fake endpoint による到達不可、protocol mismatch、401、tool error、success
- token cache 未作成、認証導線、verification URL 抽出、ブラウザ起動失敗
- 購読開始、`InitialText` / resource update、event parse、重複排除、通知モデル
- owner / repo / PR number / reason に応じた dummy launcher 起動
- 複数 PR / URI のイベント分離
- settings、ログ、artifact の secret 非露出

### 非対象

- WinUI 要素のクリックや目視判定
- トレイアイコン、XAML popup、実ブラウザ
- 実 OAuth provider でのユーザー承認
- production の GitHub webhook、PR、queue
- 実 AI agent の判断品質
- PC のスリープ・復帰

### 子 Issue

| Issue | 対象 |
|---|---|
| #220 | 現行 CI の壁時計計測、重複処理削減、Phase 1 用の時間予算 |
| #221 | 配布物 build、silent install / uninstall、version、cleanup |
| #222 | CLI、gateway、認証の Windows headless 契約 |
| #223 | enqueue から通知モデル、dummy launcher までのプロセス境界 |

## Phase 2: self-hosted Windows 実デスクトップ E2E

Phase 2 は #224 で実装する。常用開発 PC ではなく、テスト専用の Windows VM を使用する。
VM は実行前後に snapshot から復元でき、対話ログオン済み desktop session を持つことを
前提とする。

### 対象

- 正規 MSI の install / launch / uninstall
- WinUI メイン画面、ContentDialog、InfoBar、tray menu、notification popup
- 既定ブラウザ起動と起動不能時のフォールバック
- Mcp-Docker が起動する version / SHA / digest 固定の test stack
- gateway URL / Resource URI の取得
- sandbox の device flow と token cache 作成
- enqueue → thread-owl queue → subscriber notification → イベント表示
- 通知またはアプリ内操作からの dummy agent launcher 起動
- gateway / thread-owl の停止・復旧と再購読
- スリープ・復帰後の再購読
- スクリーンショット、動画、Windows Event Log、component log

### Runner 要件

- runner は個人の日常利用環境と分離する。
- workflow 開始時に既知の snapshot へ復元する。
- workflow 終了時は成功・失敗にかかわらず VM を破棄または再度 snapshot へ戻す。
- sandbox repository と最小権限の専用資格情報だけを使用する。
- Mcp-Docker の image は `latest` ではなく digest まで固定する。
- runner の OS build、Windows App SDK Runtime、既定ブラウザ、DPI、locale を
  version manifest に記録する。

Phase 2 は PR required check にしない。nightly で安定性を観測し、release 前は
`workflow_dispatch` で明示的に実行する。

## テスト資産の配置契約

cross-process E2E は unit test project から分離し、実装時に次の構成へ揃える。

```text
tests/e2e/
  README.md
  scenarios/
    <scenario-id>.json
  fixtures/
    gateway/
    subscriber/
    launcher/
  scripts/
    Invoke-E2E.ps1
    Invoke-E2ECleanup.ps1
    Test-E2EArtifacts.ps1
```

`winui3/SquirrelNotifier.WinUI3.Tests/Integration/` は、DI 可能な .NET 内の integration test
を引き続き配置する。OS process、MSI、Task Scheduler、実ファイル配置を跨ぐテストは
`tests/e2e/` が所有する。

### Scenario manifest

各 `scenarios/<scenario-id>.json` は最低限、次の情報を持つ。

```json
{
  "schemaVersion": 1,
  "id": "gateway-auth-required",
  "phase": "headless",
  "timeoutSeconds": 120,
  "components": {
    "mcp-resource-subscriber": "0.5.0"
  },
  "fixture": "gateway/auth-required",
  "expectedOutcome": "AUTH_REQUIRED"
}
```

- `id` は artifact と failure record でも同じ値を使用する。
- fixture のファイル名や本文に実 token、実 user code、実 repository を含めない。
- 時刻、port、一時パスは harness が注入し、fixture に固定しない。
- 未知の `schemaVersion` は fallback せず失敗させる。

### Fake endpoint

fake endpoint は HTTP status、MCP JSON-RPC response、接続切断、timeout を決定的に返す。
OAuth provider、token refresh、MCP server の状態機械を再実装しない。認証成功相当の
fixture は、subscriber が公開契約として返す結果だけを再生する。

### Dummy process

dummy subscriber / launcher は次を満たす。

- 受け取った引数、working directory、標準入力の状態を構造化 JSON へ記録する。
- stdout / stderr / exit code / 応答遅延を scenario から指定できる。
- shell 文字列連結を使用せず、実製品と同じ `ProcessStartInfo.ArgumentList` 境界を通す。
- 実 GitHub CLI、AI agent、ブラウザを起動しない。

## Version pin 契約

- .NET SDK は `global.json` を真実点とする。
- NuGet package は `winui3/Directory.Packages.props` を真実点とし、lock file を導入した場合は
  その内容にも従う。
- GitHub Actions は Renovate 管理下の明示 version または commit SHA を使用する。
- mcp-resource-subscriber は scenario または E2E 用 version manifest で固定する。
- Phase 2 の Mcp-Docker、container image、review component は commit SHA と image digest を
  記録する。
- `latest` を暗黙取得しない。
- version mismatch は製品テストを続けず `CONTRACT_VERSION_MISMATCH` で終了する。

互換性 matrix の更新は依存更新として独立レビュー可能にし、製品コード変更へ混在させない。

## Secret とテストデータ

### Phase 1

- GitHub Actions secret を使用しない。
- token に見える固定 dummy marker だけを secret scan 用に使用する。
- `%LOCALAPPDATA%`、環境変数、command line に実資格情報を置かない。
- production endpoint への outbound request を許可しない。

### Phase 2

- sandbox 専用資格情報を runner の secret store から環境変数で渡す。
- token、Authorization header、device code、user code を command line に渡さない。
- secret 値を assertion message、console、screenshot、artifact 名へ出さない。
- artifact 作成前に既知の secret と token pattern を scan する。
- secret scan が失敗した場合は artifact upload を停止し、
  `SECURITY_SECRET_EXPOSURE` として扱う。

## 一時領域と cleanup

各 scenario は次の専用 root を使用する。

```text
%RUNNER_TEMP%\squirrel-notifier-e2e\<run-id>\<scenario-id>\
```

設定、token cache、fixture、ログ、publish output は専用 root または専用 Windows test user
の profile に隔離する。repository root に runtime artifact を残さない。

cleanup は PowerShell の `finally` から必ず実行し、次を順番に処理する。

1. Squirrel Notifier、dummy process、subscriber の終了
2. Task Scheduler 登録の解除
3. MSI / setup bundle の uninstall
4. test user の `%LOCALAPPDATA%\SquirrelNotifier` と token cache の削除
5. fake endpoint と一時 port の解放
6. 専用 root の削除
7. process、task、製品登録、ファイルが残っていないことの検証

cleanup が失敗した場合は、元の製品テストが成功していても run を失敗させる。診断 artifact
を採取する前に削除してはならない。Phase 2 は上記に加えて VM snapshot 復元を必須とする。

## Artifact 契約

### 共通ファイル

```text
artifacts/e2e/<phase>/<scenario-id>/
  result.json
  failure.json
  versions.json
  sanitized.log
  cleanup.json
```

- `result.json`: scenario、開始・終了時刻、結果、各 phase の duration
- `failure.json`: 失敗分類、component、原因、関連 artifact
- `versions.json`: OS、SDK、runtime、CLI、commit SHA、image digest
- `sanitized.log`: secret scan 済みの統合ログ
- `cleanup.json`: cleanup 対象ごとの実行結果と残留確認

Phase 1 の成功時は job summary と `versions.json` だけを残し、失敗時 artifact は 14 日保持する。
Phase 2 は screenshot、必要に応じて動画、Windows Event Log、component log を加え、
nightly は 30 日、release 前実行は 90 日保持する。

artifact は成功判定の正本にしない。workflow の exit code と `result.json` が一致しない場合は
`TEST_HARNESS_FAILED` とする。

## Failure reason

`failure.json` は次の schema を使用する。

```json
{
  "schemaVersion": 1,
  "phase": "headless",
  "scenarioId": "gateway-auth-required",
  "category": "PRODUCT_CONTRACT_MISMATCH",
  "component": "squirrel-notifier",
  "message": "期待した AUTH_REQUIRED と異なる結果を受信しました。",
  "startedAt": "2026-01-01T00:00:00Z",
  "completedAt": "2026-01-01T00:00:10Z",
  "artifactHints": ["sanitized.log", "versions.json"]
}
```

分類は次に限定し、自由文だけで失敗させない。

| Category | 意味 | 所有先 |
|---|---|---|
| `INFRA_RUNNER_FAILED` | runner、VM、desktop session、disk 等の基盤障害 | runner 管理 |
| `DEPENDENCY_ACQUISITION_FAILED` | SDK、tool、固定 artifact の取得失敗 | CI / dependency 管理 |
| `CONTRACT_VERSION_MISMATCH` | version pin または公開 CLI / payload 契約の不一致 | 対象 component |
| `TEST_HARNESS_FAILED` | fixture、fake endpoint、assertion harness 自体の欠陥 | squirrel-notifier E2E |
| `PRODUCT_BUILD_FAILED` | publish、MSI、bundle の生成失敗 | squirrel-notifier |
| `PRODUCT_INSTALL_FAILED` | install、launch、uninstall の製品不具合 | squirrel-notifier |
| `PRODUCT_CONTRACT_MISMATCH` | 状態、error classification、通知モデル、launcher 引数の不一致 | squirrel-notifier |
| `PRODUCT_UI_FAILED` | WinUI、tray、browser を含む Phase 2 の不一致 | squirrel-notifier |
| `SECURITY_SECRET_EXPOSURE` | log、settings、screenshot、artifact への機密値露出 | 該当 component / harness |
| `CLEANUP_FAILED` | process、task、製品登録、file、VM の残留 | harness / runner 管理 |
| `TIMEOUT` | scenario 固有 timeout の超過 | 記録した component |

`INFRA_RUNNER_FAILED` であっても自動 retry はしない。再実行の判断は artifact と runner
status を確認した人間が行う。

## ローカル実行契約

子 Issue の実装は、CI とローカルで同じ entrypoint を使用する。

```powershell
# Phase 1 の全 scenario
pwsh -File .\tests\e2e\scripts\Invoke-E2E.ps1 -Phase Headless

# 1 scenario の再現
pwsh -File .\tests\e2e\scripts\Invoke-E2E.ps1 `
  -Phase Headless `
  -Scenario gateway-auth-required `
  -ArtifactsDirectory .\artifacts\e2e-local

# 明示的な後始末と残留確認
pwsh -File .\tests\e2e\scripts\Invoke-E2ECleanup.ps1 `
  -RunRoot <実行時に表示された専用 root>
```

これらの script は #221〜#223 で実装する。本書の追加時点ではまだ存在しない。

ローカル実行は管理者権限を暗黙要求しない。MSI install 等で権限が必要な scenario は開始前に
preflight し、不足時は途中まで実行せず明確な failure reason を返す。

## 障害調査手順

1. GitHub job summary で `scenarioId`、`category`、`component`、duration を確認する。
2. `versions.json` で runner image、SDK、CLI、container digest の drift を確認する。
3. `failure.json` の `artifactHints` に従い、必要な sanitized artifact だけを読む。
4. `CLEANUP_FAILED` の場合は、同じ runner で別 scenario を続行しない。
5. Phase 1 は同じ commit と scenario をローカル entrypoint で 1 回再現する。
6. dependency contract の不一致は owning repository の Issue へ切り出し、Squirrel Notifier 側で
   独自互換 hack を追加しない。
7. Phase 2 の runner 障害は VM snapshot と runner image を修復し、製品変更と同じ PR に
   混在させない。

## Required check への昇格条件

Phase 1 を required check にするには、次をすべて満たす。

- #219 と #220 が完了している。
- #221〜#223 の対象 scenario と local entrypoint が実装済みである。
- retry なしで 10 回連続成功している。
- 少なくとも 2 つの runner image 更新日を跨いで成功している。
- timeout、artifact、failure reason、cleanup、secret scan が実際の失敗ケースで検証済みである。
- 未分類失敗と既知の flaky test が 0 件である。
- [`docs/ci-wall-clock.md`](ci-wall-clock.md) が定義する PR CI 全体の wall clock budget を超えない。
- branch protection へ追加する check 名が固定されている。
- rollback 手順と一時的に required から外す判断基準が文書化されている。

導入順は `workflow_dispatch`、non-required PR check、required PR check とする。Phase 2 は
この昇格条件の対象外で、nightly / release 前 gate のまま運用する。

## 依存順

```text
#219 設計 ─┬─> #221 配布物 E2E ─┐
           └─> #222 契約 E2E ───┼─> #223 プロセス境界 E2E ─> #224 実デスクトップ E2E
#220 CI 最適化 ──────────────────┘
```

- #219 と #220 は並行着手できる。
- #221 / #222 は本書を実装契約として使用し、#220 完了後に required 化する。
- #223 は #221 / #222 の fixture、artifact、failure reason を再利用する。
- #224 は Phase 1 が安定し、Mcp-Docker の固定 test stack を利用できる状態で着手する。

## 非スコープ

- review agent の判断品質評価
- production PR に対する自動レビュー
- 自動マージ
- 各 component の責務を Squirrel Notifier 内へ再実装すること
- 初回から全 scenario を required check にすること
- CI 高速化のための runtime / SDK / toolchain downgrade
- 常用開発 PC の self-hosted runner 化

## 関連

- Epic: #184
- CI 壁時計最適化: #220 / [`docs/ci-wall-clock.md`](ci-wall-clock.md)
- Phase 1 配布物 E2E: #221
- Phase 1 CLI / gateway / 認証 E2E: #222
- Phase 1 プロセス境界 E2E: #223
- Phase 2 実デスクトップ E2E: #224
- 責務境界: #48
- 全手動レビューサイクル検証: #111
- 手動 E2E ランブック: #166
- 購読停止中のレビュー登録導線: #185
- Mcp-Docker の責務境界: scottlz0310/Mcp-Docker#158
- mcp-gateway の責務境界: scottlz0310/mcp-gateway#92
- mcp-resource-subscriber の責務境界: scottlz0310/mcp-resource-subscriber#86
- thread-owl の長時間配信停止: scottlz0310/thread-owl#117
