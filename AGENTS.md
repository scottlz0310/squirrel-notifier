## リポジトリガイドライン

### 日本語を使用すること
このリポジトリにおけるドキュメントおよびコードコメントは日本語で記述してください。英語の使用は避け、すべてのコミュニケーションを日本語で行うよう徹底してください。

### 依存関係のバージョン整合性の解決方針
RenovateによるPRでバージョン更新を提案されている場合、以下の手順で対応してください:
1. マージする場合はCI-CDパイプラインでビルドとテストが成功することを確認します。
2. 失敗している場合は、できるだけ提案のバージョンを尊重し、整合性を保つように努めます。
3. どうしても解決できない場合、Reject、Closeし、理由をコメントで説明します。

### プロジェクト構造とモジュール分割
Squirrel Notifier は `winui3/SquirrelNotifier.WinUI3` 配下に含まれます。主な機能は `Services/`、`ViewModels/`、`Helpers/`、`Models/` に分割されています。テストは `winui3/SquirrelNotifier.WinUI3.Tests` に、ソース側と同じフォルダ構成で配置されています。デプロイ用スクリプトや CI フックは `scripts/`（PowerShell）と `.ci-helper/` にあります。`squirrel-notifier.png` のようなアセットや、パッケージングに関する補足は `docs/` に置いてください。OS 固有の実験ノートは `windows_only_plan/` にまとめて、ソリューション直下が散らからないようにしてください。

### コードの置き場所

新しいコードをどこに置くかは、**状態と依存を持つかどうか**で決めます。

| 置き場所 | 基準 |
|---|---|
| `Helpers/` | **状態を持たない。** `static` クラス、または不変の型。DI されず、入力と出力だけでテストできる（判定・パーサ・フォーマッタ・計算） |
| `Services/` | **状態を持つ、または DI 対象。** インターフェイスを持つ、寿命がある、イベントを発火する、ファイル・プロセス・ネットワーク・設定にアクセスする |
| `Models/` | 複数の層で共有されるデータ構造。単一の Helper / Service の入出力型は、その型を持つクラスと同じフォルダに置いてかまいません |
| `ViewModels/` | XAML へのバインディング対象。`INotifyPropertyChanged` を実装する表示状態 |
| `*.xaml.cs` | **配線のみ。** 下記を参照 |

#### `*.xaml.cs`（code-behind）に置いてよいもの

- XAML 要素の読み書き（`TextBox.Text` への代入、`Visibility` の切り替え、`ObservableCollection` への反映）
- イベントハンドラから Helper / Service への委譲
- `DispatcherQueue` への UI スレッドマーシャリング
- `ContentDialog` やウィンドウの生成と表示

#### `*.xaml.cs` に置いてはいけないもの

- **判断** — 何を起動するか、何を表示するか、何をスキップするかの分岐
- **状態** — フラグ・カウンタ・キャッシュなど寿命を持つ値
- **I/O** — ファイルアクセス・プロセス起動・ネットワークアクセス
- **文字列の組み立て規則** — ログ文言・コマンドライン・メッセージの整形

要するに **判断・状態・I/O を code-behind に持たせない**、というのがこの規約の本体です。これらは `*.xaml.cs` がカバレッジ計測から除外されている（`Tests.csproj` の `ExcludeByFile` と `codecov.yml` の `ignore`）ために生まれる盲点であり、ここへ書くとテストを書かずに 80% ゲートを通せてしまいます。

除外設定そのものは維持します。WinUI 3 の `Window` / `UserControl` は XAML ランタイムと UI スレッドなしに instantiate できず、プレーンな xUnit プロセスでは 1 行も実行できないためです。除外を外すのではなく、**除外領域を薄く保つ**のが方針です。

#### 行数チェック

`scripts/check-code-behind-size.ps1` が `*.xaml.cs` ごとの行数上限を検証します。CI の `lint` ジョブと lefthook の pre-commit で実行されます。

上限は現在の実測値に合わせた ratchet です。抽出が進んで行数が減ったら、その PR で上限も下げてください。逆に上限を超える追加が必要な場合は、同じ PR で上限を引き上げ、理由を PR に記載してください。**引き上げを禁止するのが目的ではなく、無意識に増やせないようにするのが目的です。** epic #262 の完了時点で、実測値に余裕を足した最終値へ確定させます。

### ビルド、テスト、および開発コマンド
一度だけ次のコマンドを実行して、Lefthook Git フックを設定し NuGet パッケージを復元してください:
```powershell
pwsh -File .\scripts\setup-dev.ps1
```
WinUI 3 アプリは .NET SDK を使用してビルドしてください:
```powershell
dotnet build winui3\SquirrelNotifier.WinUI3.sln -c Release -p:Platform=x64
```
テストは xUnit で `dotnet test winui3/SquirrelNotifier.WinUI3.sln` を実行します。CI と同じフォーマットルールを適用するためにコミット前に `dotnet format winui3/SquirrelNotifier.WinUI3.sln` を実行してください。ビルド済みバイナリをタスクスケジューラーに登録するには:
```powershell
.\scripts\install.ps1 -StartMinimized
```
登録解除は `.\scripts\uninstall.ps1` を使用します。

### MCP サーバー設定（リポジトリスコープ）

リポジトリ固有の MCP サーバーは `.mcp.json` に定義し、git 追跡対象とします。個人環境に閉じた設定（`~/.claude.json` や `.claude/settings.local.json`）へ書かないでください。前者は他の開発者やエージェントに共有されず、後者はグローバルの git ignore で除外されるためです。

### コーディングスタイルと命名規則
このソリューションは `.editorconfig` を適用しています：文字コード UTF-8、改行 CRLF、インデントは 4 スペース（XML/JSON/YAML は 2 スペース）、ファイルスコープの名前空間、波括弧は改行に置く、などです。インターフェイス名は `I` で始め、プライベートフィールドは先頭にアンダースコアを付けます。`TreatWarningsAsErrors=true` のため、警告はビルド失敗につながります。型推論が明確な場合を除き明示的な型を使用してください。`using` ディレクティブは名前空間外に置き、StyleCop/Roslyn アナライザや `SecurityCodeScan` を CI で有効にしています。

### テストガイドライン
xUnit と FluentAssertions を用いて `winui3/SquirrelNotifier.WinUI3.Tests` でテストを実行します。テスト名のパターンは `MethodName_ShouldExpectation` を踏襲し、範囲検証には `[Theory]` とデータ行を活用してください（例: `SettingsServiceTests.cs`）。カバレッジは行/分岐/メソッドの各項目で 80% 以上を維持する必要があります（Codecov が PR をゲートします）。テストが一時ファイルを作成する場合は `IDisposable.Dispose` で必ずクリーンアップしてください。

### コミットとプルリクエストのガイドライン
コミットメッセージは既存の Conventional Commit（例: `feat:`, `chore:`, `fix:`, `docs:`） に従ってください。各コミットは論理的なスコープにまとめ、フックが通ることを確認してください。`--no-verify` は PR 内で正当化できる場合のみ使用してください。PR には次を含めてください: 明確な概要、関連する GitHub issue や議論へのリンク、UX 変更の場合はスクリーンショットや GIF、そしてテスト（`dotnet test`、手動のトレイ動作確認など）の説明。CI がグリーンであることを確認し、機密修正は `SECURITY.md` に従っているか確認した上でレビューを依頼してください。

### リリースノート運用
リリースページの本文は `CHANGELOG.md` を単一の原稿源として機械生成します（`scripts/extract-release-notes.ps1`）。`release.yml` がタグ push 時に該当バージョン節を抽出し、`.github/RELEASE_TEMPLATE.md`（インストール手順・前提条件）と合成して適用します。

- リリース準備 PR では `CHANGELOG.md` に該当バージョン節（`## [x.y.z] - 日付`）を追加し、csproj の `<Version>` と一致させてください（`changelog-guard` ワークフローが PR で検証します）。
- 任意で、バージョン見出しの直下に短い導入文（自由文）を書けます。書かれていればリリースページの導入段落になり、無ければ省略されます（強制ではありません）。
- `### Added` などのセクション名は生成時に日本語へマップされます。箇条書きの体裁は CHANGELOG の記述がそのまま反映されます。
- リパーパス（旧 WSL-kernel-watcher）前の履歴は `docs/CHANGELOG-archive.md` に分離しています。`CHANGELOG.md` には v0.1.0 以降のみを記載してください。
- 公開済みリリースの本文を後から貼り直す場合は `Reapply Release Notes` ワークフローを使います。既定は preview（job summary / artifact）で、`apply: true` を指定したときのみ上書きします。

### セキュリティと設定のヒント
シークレット（API トークン等）はソースコードに含めないでください。ユーザ固有の設定は `%LocalAppData%\SquirrelNotifier\settings.json` を使用することを推奨します。スクリプトは Windows ホストを前提としているため、タスクスケジューラーへ登録・変更する場合は管理者権限の PowerShell から実行してください。依存関係は Renovate 等で最新化してください。ただし、`winui3/Directory.Packages.props` でのマニフェスト変更は WinUI SDK の互換性を壊す恐れがあるため慎重にレビューしてください。
