# CI 壁時計の計測と時間予算

PR CI の壁時計を継続的に観測し、Windows 統合 E2E（#184 Phase 1）を required check へ
追加できる時間的余地を確保するための記録。Issue #220 に対応する。

## 計測方法

`CI` workflow の `timing-report` job が、run 完了後に job / step の所要時間を job summary へ
markdown 表として出力する。壁時計は run 開始から最後に完了した job までを取り、
`timing-report` 自身は baseline との比較が崩れないよう除外する。

ローカルから過去 run を再集計する場合は同じ script を使う。

```powershell
./scripts/report-ci-timings.ps1 `
  -Repository scottlz0310/squirrel-notifier `
  -RunId <run id> `
  -RunAttempt 1 `
  -SummaryPath ""
```

## Baseline（2026-07-26 / 最適化前）

直近 5 回の成功した `pull_request` の `CI` run。

| Run | 壁時計 |
|---|---:|
| 30198732424 | 560 秒 |
| 30128411814 | 586 秒 |
| 29941015539 | 518 秒 |
| 29961453102 | 514 秒 |
| 30197162596 | 498 秒 |
| **中央値** | **518 秒** |

代表 run（30198732424、壁時計 560 秒）の内訳。

| Job | 所要時間 | 主な step |
|---|---:|---|
| `security-scan` | 9m18s | Setup .NET 1m30s / Install Windows SDK 1m53s / CodeQL init 1m10s / restore 1m00s / build 1m55s / analyze 1m34s |
| `build-and-test` | 7m10s | Setup .NET 56s / Install Windows SDK 1m50s / restore 57s / build 50s / coverage 1m51s / Code Analysis 16s |
| `lint` | 3m09s | Setup .NET 1m02s / dotnet format 1m48s |

critical path は `security-scan` で、`Install Windows SDK` と `Setup .NET` が両 Windows job の
先頭で合計 3〜5 分を占めていた。

## 実施した削減

| 施策 | 対象 | 根拠 |
|---|---|---|
| Chocolatey の Windows SDK install を削除 | `build-and-test` / `security-scan` | `choco install windows-sdk-10.1` は TFM が `net10.0-windows10.0.22621.0` だった時期に windows-2025 runner 対応として入れたもの。同じ PR（#47 / commit `6de81be`）内で TFM が `10.0.26100.0` へ移行した時点で役目を終えている。`windows-sdk-10.1` は 26100 を提供せず、`WindowsSdkPackageVersion` は NuGet の参照アセンブリで解決されるため、installed SDK に依存しない |
| `8.0.x` の導入を削除 | 全 Windows job | solution は `net10.0-windows10.0.26100.0` のみを対象とする。wixproj をビルドする `release.yml` は `10.0.x` だけで動作しており、ローカル開発環境も .NET 10 SDK のみで全ビルド・全テストが通る |
| NuGet global packages folder をキャッシュ | 全 Windows job | 中央パッケージ管理で全バージョンが固定されているため、`global.json` / `nuget.config` / `Directory.Packages.props` / 各 project file の hash をキーにできる |
| coverage の test 実行を 3 回 → 1 回 | `build-and-test` | coverlet の `ThresholdType` は `line,branch,method` を同時指定できる（カンマは MSBuild のプロパティ区切りと衝突するため `%2c` でエスケープする）。閾値 80% は 3 種別とも維持し、未達時にどの種別が落ちたかもエラーへ出る |
| `Code Analysis` の再ビルドを `Build solution` へ統合 | `build-and-test` | analyzer 設定（`EnableNETAnalyzers` / `AnalysisMode=All` / `EnforceCodeStyleInBuild`）は csproj 側にあり、両 step の差分は `TreatWarningsAsErrors` / `RunCodeAnalysis` の指定だけだった。両方を 1 回目のビルドへ渡して同じ検出範囲を保つ |
| `dotnet-format` グローバルツールの導入を削除 | `lint` | `dotnet-format` は .NET 5 時代の非推奨パッケージで、導入すると SDK 組み込みの `dotnet format` を隠す。lefthook の pre-commit は既に SDK 組み込み版を使っており、CI をそちらへ揃える |
| superseded run の自動 cancel | workflow | `pull_request` の後続 push で先行 run を打ち切る。`main` / `develop` への push は各コミットの検証記録を残すため打ち切らない |

## 維持した品質ゲート

高速化のために次を弱めていない。

- `global.json` の .NET SDK、`Directory.Packages.props` の依存、runner image を下げていない
- `dotnet format --verify-no-changes`、solution build、unit test、coverage 閾値（line / branch / method 各 80%）、
  Roslyn analyzer、StyleCop、Roslynator、SecurityCodeScan、CodeQL、MSI MajorUpgrade 検証をすべて実行している
- retry や `continue-on-error` を追加していない

## 変更後の実測（2026-07-26）

HEAD `43195c32` に対する `pull_request` run `30199924023` の 5 attempt。

| Attempt | 壁時計 | 備考 |
|---|---:|---|
| 1 | 392 秒 | cold cache（当該キーの初回 run） |
| 2 | 342 秒 | warm cache |
| 3 | 378 秒 | warm cache |
| 4 | 368 秒 | warm cache |
| 5 | 341 秒 | warm cache |
| **中央値** | **368 秒** | baseline 518 秒に対し **29.0% 短縮** |

warm cache のみ 4 回の中央値は 355 秒（31.5% 短縮）。cache キーは `global.json` /
`nuget.config` / `Directory.Packages.props` / project file の hash で決まるため、
cold cache になるのは依存更新 PR と version bump に限られる。

critical path は `security-scan`（warm 331〜372 秒）で、その内訳は Setup .NET 64〜77 秒、
CodeQL init 62〜78 秒、CodeQL build 96〜109 秒、CodeQL analyze 75〜97 秒。CodeQL 関連が
約 250 秒を占め、ランナー変動が ±20% あるため、#220 の目標である 30% 短縮
（363 秒以下）には届いていない。**残り約 1% の短縮は #220 で継続する。**

主要 step の変化（cold → warm）。

| Step | 変更前 | cold | warm |
|---|---:|---:|---:|
| `Install Windows SDK` | 110 秒 | 削除 | 削除 |
| `Restore for CodeQL` | 60 秒 | 71 秒 | 4〜8 秒 |
| `Install dependencies` | 57 秒 | 58 秒 | 4〜13 秒 |
| `Run dotnet format` | 108 秒 | 103 秒 | 39〜46 秒 |
| `Run tests with coverage` | 111 秒 | 41 秒 | 37 秒 |

## 検討したが採用しなかった施策

### CodeQL の `build-mode: none`

`Restore for CodeQL` / `Build for CodeQL` / WinRT generator 回避ステップを不要にし、
overlay database も有効化できるため候補に挙げたが、**不採用**とした。

同一ソースに対する manual build-mode との実測比較は次のとおり。

| 区分 | manual | none |
|---|---:|---:|
| リポジトリ内の手書き C# ソース | 144 files / 21,687 LOC | 144 files / 21,687 LOC |
| ビルド生成コード（XAML codegen、source generator 出力） | 31 files / 8,204 LOC | 0 |
| 依存パッケージ / SDK 由来 | 3 files / 77 LOC | 0 |
| CodeQL `rules_count` | 52 | 52 |
| CodeQL `results_count` | 0 | 0 |
| extractor 診断 | 0 件 | 0 件 |
| `security-scan` 壁時計 | 331〜372 秒 | 325 秒 |

不採用の理由。

1. XAML codegen と source generator が生成する 8,204 LOC、および依存パッケージ由来の
   ソースが解析対象から確実に欠落し、CodeQL の解析完全性が下がる。
2. 手書きソースの抽出範囲と LOC は完全に一致したが、dataflow の同等性は実証できない。
   両モードとも検出 0 件のため差が現れず、canary で単一 rule の発火を確認しても、
   生成コード境界を含む実コードの dataflow 同等性の証明にはならない。
3. CodeQL bundle 同梱のオプション説明自体が buildless 抽出について
   "will generally yield less accurate analysis results, and should only be used in cases
   where it is not possible to build the code" と明記している。
4. 削減幅は想定より小さい。`Build for CodeQL`（約 107 秒）が消える一方で
   `Perform CodeQL Analysis` が 75〜97 秒から 145 秒へ増え、正味の短縮は約 40〜50 秒に留まる。

品質ゲートの完全性を優先し、`build-mode` は manual のまま維持する。

### PR CI から CodeQL を外す

`push` / schedule のみで実行すれば critical path から約 250 秒を除去できるが、
#220 の制約「CodeQL を省略しない」に反するため検討対象外とした。

## Phase 1 E2E の時間予算

#184 Phase 1 の headless E2E を required check にする際は、次を超えないこと。

| 項目 | 予算 | 現状 |
|---|---:|---|
| 既存 3 job の critical path | 360 秒 | 368 秒（未達。#220 で継続） |
| Phase 1 E2E job 単体 | 330 秒 | 未実装 |
| E2E 追加後の PR CI 全体の壁時計 | 420 秒 | 未実装 |

予算の根拠は baseline 中央値 518 秒の 30% 短縮（#220 の目標）で、E2E job を既存 job と
並列に置いても全体を押し上げないことを条件としている。

運用規約。

- Phase 1 を required check へ昇格させる前に、既存 3 job の critical path が 360 秒以内へ
  収まっていること。本書の作成時点では 368 秒で未達のため、#220 の残作業が先行する。

- E2E job は `needs` で既存 job の後段へ直列化しない。publish payload や MSI が必要な場合は
  E2E job 内で生成するか、artifact 経由で受け取ったうえで全体予算を実測で確認する。
- 予算超過が観測された場合、Phase 1 を required check へ昇格させない。先に本書の
  「実施した削減」と同じ手順で計測・削減し、超過要因を記録する。
- 予算を守るために target SDK / runtime / toolchain を下げたり、既存の品質ゲートを
  省略したりしない。予算側を見直すか、E2E の対象を分割する。

## 関連

- Epic: #184
- CI 壁時計最適化: #220
- 統合 E2E 設計: [`docs/windows-integration-e2e.md`](windows-integration-e2e.md)
