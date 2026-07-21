# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> リパーパス（旧 WSL-kernel-watcher）前の v3.1.0 以前の履歴は [`docs/CHANGELOG-archive.md`](docs/CHANGELOG-archive.md) を参照してください。

## [Unreleased]

### Added
- mcp-gateway の初回認証（RFC 8628 device flow）をアプリ内から開始できる導線を追加した（#183）。Settings の Gateway URL 欄に「ログイン」ボタンを、認証エラー時のメイン画面に「mcp-gateway にログイン」ActionButton 付き InfoBar を追加し、設定済みの `SubscriberCommandPath` と `GatewayUrl` を使って `mcp-resource-subscriber --login --url <gateway>` を安全に起動する。OAuth / device flow / token refresh は Squirrel Notifier に再実装せず、subscriber の既存 device flow とトークンキャッシュ（`%LOCALAPPDATA%\mcp-resource-subscriber\tokens.db`）を利用する。subscriber が出力した `verification-uri` を既定ブラウザで開き、ブラウザ起動に失敗しても認証を中断せず、承認 URL と user code をコピー可能な UI で提示する。ログインの進行中・成功・失敗・キャンセル・タイムアウトを区別して表示し、認証成功後に購読が停止中／Error なら再購読を自動で開始する。subscriber 未検出・`--login` 非対応の古いバージョン（v0.3.0 未満）・gateway 到達不可・device flow 拒否は原因を含むメッセージで通知する。アクセストークン・リフレッシュトークン・Authorization ヘッダーは設定・ログ・プロセス引数へ出力しない。プロセス起動・URL 抽出・ブラウザ起動失敗・終了コード・キャンセル・タイムアウトを DI 可能な単体テスト（`McpLoginService` / `DeviceLoginOutputParser`）で検証する。README に GUI / CLI の初回認証手順とトラブルシューティング（トークン優先順位・gateway 再構築後の `--logout` → `--login`・再購読）を追記した

### Fixed
- レビューイベントを受信するとアプリがプロセスごと異常終了する問題を修正した（#199）。`H.NotifyIcon.WinUI 2.4.1` の `TrayPopup` は WinUI3 でも `Popup` を生成して `IsOpen = true` する実装で、生成された `Popup` に `XamlRoot` が設定されないため、表示のたびに `E_UNEXPECTED`（stowed exception `0xC000027B` / 障害モジュール `CoreMessagingXP.dll`）でプロセスが落ちていた。`TrayPopup` が専用ウィンドウ方式へ書き直された `2.5.0-beta.1` を採用し、併せて `TaskbarIcon.TrayPopup` の `<Popup>` ラッパーを外して `ReviewNotificationPopup` を直接ホストするようにした（`Popup` を渡すとライブラリがそれをそのまま採用してしまうため）。通知表示に失敗した場合もプロセスを落とさず、原因をログへ記録したうえでバルーン通知へフォールバックする。`H.NotifyIcon.WinUI` は正式版 `2.5.0` がリリースされ次第、安定版へ戻す
- レビューイベントのトースト通知ボタンが `AppNotificationManager.Register()` 失敗時に反応しない場合がある問題を修正した（#181）。トレイ実装と通知表示を `H.NotifyIcon.WinUI` に統合し、レビューイベントはトレイアイコン付近の `TrayPopup` に「PRを開く」「レビューする」「アプリを開く」ボタンを表示する。従来の `AppNotificationManager` 登録・activation 処理、`Shell_NotifyIcon` / WndProc / 自前コンテキストメニュー、通知無効警告 InfoBar を削除した。左クリックでメイン画面を開く操作、右クリックメニュー、購読状態に応じたアイコン変更、操作不要の標準通知は維持する。`H.NotifyIcon.WinUI 2.4.1` が要求する `WinRT.Runtime 2.2` と整合させるため、Windows SDK .NET 参照は同じ 26100 系の `10.0.26100.84` へ更新した
- `claude` launcher プリセットで実行中の phase 表示（progress event）がライブログウィンドウへ逐次反映されず、セッション終了時にまとめて表示される問題を修正した（#187）。claude の print mode 既定（`--output-format text`）は最終応答しか stdout へ出力しないため、スキルが echo する `@squirrel-progress` マーカーを実行中に取得できなかった。既定引数へ `--verbose --output-format stream-json` を追加し（`-p` + stream-json は CLI 仕様で `--verbose` が必須）、新設の `ClaudeStreamJsonEventExtractor` が stream-json イベントの `tool_result` からマーカーを抽出して既存の progress event contract へ接続する。assistant テキスト（ナレーション・最終応答）は通常ログとして逐次表示し、マーカー以外のツール出力（ファイル全文等）はライブログへ流さない。malformed JSON・未知イベントは生の行を通常ログとして扱い、レビュー実行は失敗させない。既存の未変更 `claude` プリセットは一回限りの migration（`ClaudeStreamJsonMigrated`）で更新し、カスタマイズ済みの command / arguments は変更しない
- AI エージェント起動のコマンド解決と Windows shim の起動規約を、レビュー起動とレートリミット取得で共通化した（#186 の残タスク）。PATH / PATHEXT 解決を共通の `CommandPathResolver` に集約し、launcher 側も PATHEXT に従ってコマンドを解決するようにした。`.cmd` / `.bat` シムは `CreateProcessW` の暗黙の cmd.exe 起動（`ArgumentList` の引用規約と cmd.exe のパース規則が一致せず、引数内のメタ文字が再解釈されうる経路）に頼らず、共通の `AgentProcessStartInfoFactory` が `cmd.exe /d /s /v:off /c` で明示的にラップし、実行パスと各引数を環境変数の引用符内展開で安全に受け渡す。引用符・改行を含み安全に渡せない引数は起動前に明示エラーで拒否する。`.exe` / `.cmd` / `.bat` の実プロセス起動テストと、タスクスケジューラ起動相当の初期 cwd（System32）から dummy agent を起動する回帰テストを追加した
- タスクスケジューラー等から launcher を起動した際に不定な current directory（例: `C:\Windows\System32`）を継承し、Codex の Git repository check や reviewed 側の checkout 操作が失敗する問題を修正した（#186）。reviewer は Settings 配下の専用 workspace、reviewed は Settings の `owner/repo=絶対パス` mapping で解決した Git checkout を明示的な `WorkingDirectory` として起動する。reviewed の mapping 不備・非 Git directory・システム／インストール先 directory はプロセス起動前に拒否する。Codex reviewer 既定引数には `--skip-git-repo-check` を追加し、未変更の旧既定値だけを一回限りで移行する。永続ログには実効 cwd・解決済み executable・実行形式・終了コードと、非ゼロ終了時のマスク済み stderr 要約を残す
- `agy` launcher プリセットが CLI 内部の既定 `--print-timeout 5m` で終了し、squirrel-notifier の Launcher Timeout（既定30分）より先にレビューが失敗する問題を修正した（#180）。reviewer / reviewed の既定引数へ `--print-timeout 30m` を追加し、既存の未変更プリセットは一回限りの migration で更新する。カスタマイズ済みの command / arguments は変更しない

## [0.5.2] - 2026-07-14

### Fixed
- codex レートリミット取得が PATHEXT 非解決で `CommandNotFound` になる問題を修正した（#177）。`CodexAppServerRateLimitClient` は `FileName="codex"` かつ `UseShellExecute=false` で `Process.Start` していたため、npm 経由でインストールされた `codex.cmd` シム環境では Win32 `CreateProcessW` が `.exe` のみを暗黙補完し、ターミナルでは実行できるのに `ERROR_FILE_NOT_FOUND` で失敗していた。PATH / PATHEXT を自前で解決し、`.cmd` / `.bat` が見つかった場合は `cmd.exe /c` 経由で起動するようにした
- 「トースト通知が無効です」という警告文言が誤りだったため、README と InfoBar・トレイバルーン通知の文言を修正した。`AppNotificationManager.Register()` の `Insights.Resource.dll` 読み込み失敗（#169、[microsoft/WindowsAppSDK#6071](https://github.com/microsoft/WindowsAppSDK/issues/6071) — Microsoft 側の self-contained 配布における既知の未解決バグ）はボタン操作（`NotificationInvoked`）の受信にのみ影響し、`AppNotificationManager.Show()` によるトースト表示自体は妨げない。実機ログ・実機確認でも `Register()` 失敗時にトースト自体は表示されることを確認した

## [0.5.1] - 2026-07-14

### Added
- UIスレッド・AppDomain・未observeなTaskの未処理例外をグローバルに捕捉し、`winui3.log` へ記録するようにした（#174）。従来はグローバル例外ハンドラが存在せず、真に未捕捉の例外が発生した場合はログに一切証跡が残らずアプリが終了していた。原因不明の「予期しないエラー」報告の切り分けに必要な最低限の証跡を残す
- 旧形式ステータスライン警告とトースト通知無効警告の InfoBar に `ActionButton` を追加し、それぞれ `docs/statusline-integration.md` と README の既知の制約セクションを外部ブラウザで開けるようにした（#174）。従来は参照先をドキュメント名で案内するのみでクリック導線がなかった
- codex レートリミット取得不可時のエラーメッセージを原因ごとに分離した（#174）。`CodexAppServerRateLimitClient.CaptureWithFailureReasonAsync` が「CLI 未検出」「タイムアウト」「その他（未ログインの可能性を含む）」を区別して返すようにし、`codex` コマンド未インストールやタイムアウトの場合まで一律「ログイン済みか確認してください」と表示していた問題を解消した。JSON-RPC error の code/message は codex CLI バージョンにより変わりうるため、確実に判別できないケースは断定的な理由を出さない。「CLI 未検出」は `Win32Exception.NativeErrorCode` が `ERROR_FILE_NOT_FOUND` / `ERROR_PATH_NOT_FOUND` の場合のみとし、アクセス拒否等コマンド自体は存在するケースを誤って「未インストール」と案内しないようにした

### Fixed
- MSI インストーラー / セットアップ Zip による正規インストールでも「トースト通知が無効です」警告が発生しうることを README と InfoBar の文言に反映した（#174）。本アプリは `WindowsPackageType=None` かつ self-contained でビルドされており、正規インストールも実行形態としては unpackaged であるため、`AppNotificationManager.Register()` の `Insights.Resource.dll` 読み込み失敗（#169）は配布形態に関わらず発生しうる。#169 時点の「正規配布物では発生しない想定」という記述は誤りだった

## [0.5.0] - 2026-07-14

### Added
- 開発ビルド（unpackaged 実行）で `AppNotificationManager.Register()` が `Insights.Resource.dll` 読み込み失敗（8007007E）で失敗した場合に、トレイバルーン通知と InfoBar（「トースト通知が無効です」）でフォールバック表示するようにした（#169）。従来は WARN ログのみでユーザーへのフィードバックがなく、トースト通知が無効なことに気づきにくかった。正規配布物（MSI / セットアップ Zip）ではこの制約は発生しない想定で、`README.md` の手動起動セクションに既知の制約として明記した
- statusline snapshot が旧形式（`schemaVersion` 等を欠く resetAt-only）のままの場合に警告する InfoBar を追加した（#168）。旧形式はレートリミット一覧には引き続き表示されるため気づきにくいが、`RateLimitSnapshotService` は常に `null` を返すため Auto-Pause（#147）が silent に無効化されていた。`RateLimitStatusParser.IsLegacySchema` で検出し、「レートリミット状態」セクションの「更新」実行時に対象エージェント名を明示する。`docs/statusline-integration.md` に旧形式 → 新スキーマの移行手順を追記した

### Fixed
- レートリミット一覧の「更新」ボタンで Auto-Pause gate が再評価されず、fresh な使用率 95% 未満への回復が反映されない不具合を修正した（#167）。`OnRefreshRateLimitClick` に reviewer / reviewed 両スロットの `rateLimitAgentId` を対象とした `AutoPauseGate.Evaluate` 呼び出しを追加し、「更新」実行時点で Paused の解除（および開始）を InfoBar に即時反映する。実行中プロセス・MCP subscription・thread-owl queue には作用しない
- `ReviewLauncherServiceTests.StartSession_ShouldNotStartProcess_WhenCancelledImmediatelyAfterStartSession` が稀に失敗する不安定性を修正した。`RunSessionAsync` の先頭で `LogAsync`（`File.AppendAllTextAsync`）が OS キャッシュヒット等で同期的に完了すると、コンパイラの完了済みタスク最適化により `StartSession` の呼び出しスレッド上でプロセス起動直前まで同期的に進んでしまい、直後の `Cancel()` が開始前レースに間に合わないことがあった。`RunSessionAsync` の先頭に `Task.Yield()` を追加し、`StartSession` が確実に呼び出し元へ制御を返してから本体処理を継続するようにした

## [0.4.0] - 2026-07-11

### Added
- Codex のレートリミットを App Server 経由で取得できるようにした（#163、spike #157）。`codex app-server`（stdio JSON-RPC）を snapshot 取得時にオンデマンド起動し、`account/rateLimits/read` の結果を共通スキーマへ正規化する（`rateLimitsByLimitId` 優先・`rateLimits` フォールバック、`{limitId}:primary` / `{limitId}:secondary` の安定 ID、`windowDurationMins` からのラベル導出）。statusline 連携が不要になり、レートリミット監視対象として codex を選択できる。App Server 経由の snapshot は取得時刻が観測時刻になるため常に fresh であり、ヘッドレス実行でも Delta と Auto-Pause の解除判定が機能する。未ログイン・起動失敗・タイムアウト・JSON-RPC error は「取得不可」の正常系として扱い、レビュー実行を妨げない。認証ファイルの読み取り・TUI 出力のパース・consume 系 API の呼び出しは行わない
- `LauncherAgentDefinition` に progress event 対応度（`ProgressEventSupport`）を追加した（#151）。#143 で確定した contract では consumer 側の挙動は「構造化イベントを出力する統合が存在するか」のみで決まるため `Structured` / `None` の 2 値とし、claude は `Structured`（スキルへの progress スニペット組み込みが存在）、codex / agy / copilot / カスタム設定は `None`。ライブログウィンドウは未対応プリセットの実行開始時に「進捗表示に未対応（ログのみ）」であることを明示し、イベントが来ないことを異常に見せない。対応度は初期表示のヒントであり、`None` でも `@squirrel-progress` イベントを受信すれば phase 表示へ切り替わる（カスタム構成の producer を妨げない）
- 危険水域で新規エージェント起動を停止する Auto-Pause を追加した（#147）。起動する launcher スロットのプリセットに対応する agent（#149 の rateLimitAgentId）の fresh な snapshot を起動直前に評価し、いずれかの limit の使用率が 95% 以上なら Paused へ遷移して新規起動を拒否する。gate は agent 単位で独立し、rateLimitAgentId を持たないプリセット（copilot / カスタム）は常に許可する。解除は fresh な snapshot で 95% 未満を確認した場合のみで、stale / missing data や resetAt 通過だけでは解除しない。Paused の理由（agent・limit・使用率・観測時刻・リセット時刻）は起動時ダイアログ・メイン UI・ライブログウィンドウに表示し、確認ダイアログ付きの「今回だけ起動を強行」で 1 回だけ override できる。実行中プロセス・MCP subscription・thread-owl queue には作用しない。運用手順は `docs/auto-pause.md` を参照
- ライブログウィンドウへレートリミット燃料ゲージを統合した（#146）。実行中の launcher に対応する agentId を最優先し、同一エージェント内では使用率が最大の枠を既定表示する。複数 agent / limit は ComboBox で手動切替でき、使用率・残量・リセット時刻・観測時刻・鮮度・実行前後の Delta を同一領域で確認できる。70% 以上を注意、90% 以上を危険としてテキストと InfoBar で明示し、stale・欠損・旧スキーマ・不整合な agentId・snapshot ファイルの読み取り失敗は「取得不可」の正常系として扱い、レビュー実行本体を中断しない。Delta は開始／終了 snapshot が有効な場合のみ表示し、それ以外は取得不可の理由を表示する
- レートリミットスキーマを使用率・鮮度・Delta対応へ拡張した（#145）。`RateLimitStatusPayload` / `RateLimitInfo` に `schemaVersion` / `agentId` / `observedAt` / `limits[].usedPercentage` を追加（agy の `remaining_fraction` はサンプルスクリプト側で使用率へ正規化）。`schemaVersion` 等を持たない旧形式（resetAt-only）の payload は引き続き通知予約用途で読み取れるが、使用率・Delta・freshness 判定の対象外として扱う。新スキーマの snapshot 取得には `RateLimitSnapshotService`、2つの snapshot 間の使用率差分（Delta）計算には `RateLimitDeltaCalculator` を追加し、`RateLimitDeltaUnavailableReason`（欠損・stale・reset 境界跨ぎ・usedPercentage 欠損等）により「取得不可」を例外ではなく正常系として扱う。snapshot の鮮度判定は `RateLimitFreshnessPolicy` が担い、閾値は Settings の `RateLimitFreshnessThresholdMinutes`（既定15分）で変更できる。**設計上の制約**: launcher が起動するのは `claude -p` 等のヘッドレス実行であり statusline はインタラクティブセッションの表示機構のため、ヘッドレス実行前後で fresh な snapshot が得られず Delta が「取得不可」になることが多い。Delta は best-effort と位置づけ、Claude Code の `Stop` / `SessionEnd` hooks も rate-limit snapshot を渡さないため代替経路にできない（#159）。claude-code / agy のサンプルスクリプトと `docs/statusline-integration.md` を新スキーマへ更新した

### Added
- エージェント実行ライブログウィンドウを追加した（#144 第2弾）。「レビューする」「レビューに対応」の実行時に、従来のモーダルダイアログ（実行中 ContentDialog + 終了後の結果ダイアログ）に代えて専用サブウィンドウを表示し、stdout / stderr / progress を色分けした逐次ログ、phase 進捗（ProgressBar、非対応ランチャーは indeterminate）、Verdict と terminal 状態（InfoBar）をリアルタイム表示する。ウィンドウは現在モニターの work area 内・右下へ DPI を考慮して配置され、最前面ピン留めトグルを持つ。成功時は 3 秒後に自動クローズ（Settings の「ライブログ自動クローズ」トグルで無効化可能）、失敗・キャンセル・タイムアウト時は診断のため保持する。「キャンセル」ボタンと実行中のウィンドウクローズで実行をキャンセルできる（従来ダイアログのキャンセル動作を踏襲）。イベント反映は DispatcherQueue への coalescing バッチ化で UI 更新頻度を抑制する

### Added
- エージェント実行ライブログウィンドウ（#144）の表示ロジック層を追加した（第1弾。Window / XAML と MainWindow 統合は次の PR）。`AgentExecutionViewModel` が #143 の型付きイベントを表示状態（phase 進捗・Verdict・terminal 状態・ログ行）へ変換し、行数上限 1000 行の rolling buffer で長時間実行でも UI メモリを無制限に増やさない。表示前の各行には ANSI エスケープ・制御文字の除去（`AnsiControlSanitizer`）と機密値マスキング（`SecretMasker`）を適用する。マスキング対象は「既知トークン形式のパターン（GitHub PAT / sk- 系 API キー / Bearer ヘッダ）」と「squirrel-notifier 自身が参照する `MCP_PROBE_AUTH_TOKEN` の値」に明示的に限定し、ルール外の機密値は保護対象外であることを `docs/live-log-window.md` に明記した。あわせて work area 内右下へ clamp 配置する `WindowPlacementCalculator` と、成功時自動クローズの設定（`LiveLogAutoCloseEnabled`、既定有効）を追加した

### Fixed
- reviewed launcher 既定値のスキル呼び出し名が実在しない `/thread-owl-review-cycle` になっており、デフォルト設定のまま「レビューに対応」を実行しても意図したレビュー対応サイクルが起動しない不具合を修正した（#150）。`AppSettings.ReviewedLauncherArguments` の既定値と `LauncherAgentCatalog` の claude プリセットを実在するスキル名 `/review-raven-thread-owl-cycle` へ修正。旧既定値のまま使っている既存ユーザーは一回限りの migration（`ReviewedLauncherSkillMigrated`）で新既定値へ移行し、カスタマイズ済みの値は変更しない。この migration はプリセット判定（`LauncherPresetsMigrated`）より先に実行され、`ReviewedLauncherPresetId` が「カスタム」に誤判定されることを防ぐ

### Added
- エージェント実行イベントのストリーミング基盤と構造化 progress event contract を導入した（#143）。`ReviewLauncherService` がプロセス終了後に stdout / stderr を一括取得していた方式を行単位の逐次読み取りへ変更し、`AgentExecutionSession` から stdout / stderr / progress / completed の型付きイベントを実行中に購読できるようにした（`IReviewLauncherService.StartSession`。既存の `LaunchAsync` と `LauncherResult` の意味は維持）。progress event は stdout に混在する行頭マーカー `@squirrel-progress ` 付きの 1 行 JSON（schemaVersion / phaseIndex / totalPhases / phaseLabel / message / verdict / timestamp）とし、phase はエージェント非依存の汎用構造（0 始まり index / total / label）で phase 数を固定しない。マーカー不一致・malformed JSON・未知 schemaVersion の行は通常ログとして安全に流し、実行は失敗させない。成功・失敗・キャンセル・タイムアウトは区別された terminal event として通知する。producer 側の統合は `docs/progress-event-contract.md` とスキル組み込み用スニペット `docs/samples/skill-progress-snippet.md` を参照（statusline 連携と同じく、スキル定義への適用はユーザーの dotfiles / skills 側で行う）

### Changed
- Launcher Timeout の既定値を 5 分から 30 分へ、設定上限を 5 分から 2 時間へ変更した（#143）。レビューサイクルは 5 分を大きく超えることがあり、「長時間実行の可視化」という親 Epic #142 の前提と矛盾していたため。既存ユーザーの保存済み設定値は変更されない

### Added
- reviewer / reviewed launcher スロットの実行エージェントを claude 固定から、claude / codex / agy / copilot のプリセットから選択できるようにした（#149）。`LauncherAgentCatalog` にエージェントごとの既定コマンド・引数テンプレート（codex / agy / copilot はスキル呼び出し機構を持たないため、プロンプト全文をテンプレートに埋め込む方式）と、レートリミット監視 agentId（`RateLimitAgentCatalog`）への対応付けを集約した。Settings UI にスロットごとのプリセット選択 ComboBox を追加し、選択時に command / arguments を既定値で埋める一方、従来どおりの自由編集も維持。保存時に現在の command / arguments を各プリセットの既定値と突き合わせて一致状況を判定し、どれとも一致しなければ「カスタム」として扱う。既存ユーザーの設定は一回限りの migration（`LauncherPresetsMigrated`）で移行時点の一致状況から初期プリセットを判定する。`SettingsService.ResolveLauncherRateLimitAgentId(LauncherRole)` により、launcher スロットからレートリミット監視 agentId を解決できるようにし、Auto-Pause（#147）が利用できるようにした。各エージェントへの MCP サーバー接続設定自体は Mcp-Docker の責務であり squirrel-notifier のスコープ外であることを `docs/launcher-agent-presets.md` に明記した。また、codex 等スキル機構を持たないエージェントが標準入力の EOF 待ちでハングする既知の問題（[openai/codex#20919](https://github.com/openai/codex/issues/20919)）を回避するため、`ReviewLauncherService` は起動直後に標準入力を閉じるようにした

## [0.3.0] - 2026-07-06

### Added
- レートリミット監視対象の CLI エージェントを Settings で選択できるようにした（#139）。「レートリミット状態」セクションに claude-code / agy（Antigravity CLI）のチェックボックスを追加し、選択したエージェントの statusline フックが書き出すローカル JSON ファイル（`%LOCALAPPDATA%\SquirrelNotifier\ratelimit-status\<agentId>.json`）を「更新」時に読み取って表示する。調査の結果 `ratelimit://` を提供する MCP サーバーは存在せず、レートリミット情報は各 CLI エージェント自身の statusline フックからローカルに取得する必要があることが判明したため、既存の MCP `resources/read` 経由の取得（`ratelimit://` URI、サーバー側の将来対応に備え維持）とは別に、ローカルファイル経由の取得経路を追加した。statusline スクリプト自体の拡張（レートリミット検知時のファイル書き出し）はユーザーの dotfiles 側で行うため、`docs/statusline-integration.md` と claude-code / agy 向けのサンプルスクリプトを追加した。エージェント定義は `RateLimitAgentCatalog` に集約し、将来の追加・削除が容易な構成にした。codex は statusline フックが外部コマンドに JSON を渡さず現時点で技術的に対応不可能なため、対応待ちとして選択不可の状態で表示する

### Changed
- 起動ロールの設定切替を廃止し、イベント行で両ロールのアクションを提供するようにした（#127）
  - Settings の「起動ロール」ラジオボタンと settings.json の `launcherRole` を削除。ランチャースロットは「どのボタンを押したか」だけで決まるため、`re-review-requests` の reviewer 強制ロジックも不要になり削除
  - イベント行のアクションを「レビューする」（reviewer スロット起動）と「レビューに対応」（reviewed スロット起動）の 2 つの `SplitButton` に集約。primary クリックで起動、flyout の「コマンドをコピー」で従来どおり起動コマンドをクリップボードへコピーできる
  - トースト通知の起動ボタンは推奨ロールに基づき「レビューする」（reviewer）を表示する。現行の購読イベント（`opened` / `synchronized` / `re-review-requested`）はいずれも次のアクションが reviewer side のため。reviewed side を推奨するイベント種別（レビューが投稿された等）は queue に未定義のため、未知の reason では起動ボタンを出さず「アプリを開く」で行 UI へ誘導する

### Added
- Recent review events の各行に ✕（片付ける）ボタンを追加し、対応が完了した PR のイベントを一覧から除去できるようにした。除去は行（eventId）単位で、同一 PR の後続イベントは通常どおり表示される。イベント一覧はアプリ再起動時に復元されないため片付け状態の永続化は行わない（#128）

### Changed
- Recent review events のイベント行キャプションに PR 番号を表示するようにした（`owner/repo #123` 形式）。キャプションは `HyperlinkButton` になり、クリックで PR を直接開ける（URL は既存の `UrlValidator.IsSafeGitHubUrl` で検証）。これに伴い行の「PRを開く」ボタンは削除し、行 UI のボタン過密を緩和した（#129）。レビュー対応として、`PrNumber` が 0 以下（JSON 未指定等）の場合は `owner/repo #0` のような無効なキャプションにならないよう `Repository` のみへフォールバックするようにし、`AutomationProperties.Name` の固定文字列指定を削除して `Content`（`PrCaption`）がそのままアクセシブル名として使われるようにした（スクリーンリーダーで全リンクが同一名に聞こえる問題を解消）

### Fixed
- レビューイベントのトースト通知で「アプリを開く」等のボタンを押しても反応しない不具合を修正。実行中プロセスで通知アクティベーションを受け取るには `NotificationInvoked` ハンドラの登録を `AppNotificationManager.Register()` より前に行う必要があるため、購読順序を入れ替えた。また、アプリ未起動時に通知ボタンから新プロセスが起動された場合は `NotificationInvoked` が発火しないため、`OnLaunched` で activation kind が `AppNotification` の場合に起動引数（`openUrl` / `launchReview` / `openApp`）を処理し、ウィンドウを表示するようにした（#130）。レビュー対応として、`NotificationService.Initialize()` を `Register()` より前に呼ぶよう順序を変えたことで、self-contained モードで `Insights.Resource.dll` が見つからない環境では `Initialize()` 側でも同じ `COMException` が発生しうるため、`Register()` と同条件で捕捉して警告ログに落とし、起動クラッシュを防ぐようにした

## [0.2.0] - 2026-07-05

### Fixed
- mcp-resource-subscriber が `AUTH_LOGIN_REQUIRED`、`invalid_token` などの再認証を要するエラーを返した場合、汎用の接続エラーではなく「認証が必要です」と通知するように修正（#113）。レビュー対応として、認証必須バルーンの文言重複を解消し、非ゼロ終了時の例外メッセージに stdout / JSON パース失敗情報を含めて原因特定しやすくし、401 判定時のメッセージに `MCP_PROBE_AUTH_TOKEN` 設定不備の可能性も併記するように調整。さらに、serverUrl のポート番号や resourceUri のパスセグメントとして単独の `401` が現れるケースでも誤って認証エラーと分類されないよう、`GetErrorInfo` を構造化された `result.ErrorCode` 優先で判定する設計に変更。`RESOURCE_NOT_FOUND` 等の意味が確定した非認証コードのみホワイトリストで legacy マッチングをスキップし、`INTERNAL_ERROR` / `SUBSCRIPTION_FAILED` のような詳細不明な汎用コードでは stderr の認証エラー詳細を見逃さないよう legacy マッチングにフォールバックする。さらに、`SUBSCRIPTION_FAILED` は実際には詳細を握りつぶし JSON stdout に resourceUri 等の URL・URI のみを返す出力契約であるため、legacy マッチングの判定対象を stdout 全文ではなく stderr / result.FinalText のみの診断テキストに限定し、URI パスセグメントとしての `401` による誤判定も回避する（#120）
- MSI を同一バージョンで再ビルドした場合でも、旧 ProductCode の製品を MajorUpgrade で置き換えるように修正。生成 MSI の Upgrade table を CI とリリース時に検証する回帰チェックも追加（#117）
- single-instance ガードが無く、アプリを 2 回起動すると 2 インスタンスが同時に常駐して同一 Resource URI を並行購読してしまう不具合を修正。Windows App SDK の `AppInstance.FindOrRegisterForKey` + `RedirectActivationToAsync` による instance redirection を導入し、2 個目の起動はアクティブ化イベントを既存インスタンスへリダイレクトして自身は起動せず終了する。既存インスタンス側はリダイレクトを受け取るとウィンドウを前面化する（#116）
- `queue://review/*` の購読ループで、待機時間内にイベントが無い正常な満了（`route=timeout` / `errorCode=NOTIFICATION_TIMEOUT`）を購読失敗として扱い、リトライを消費して最大リトライ超過で購読が恒久停止していた不具合を修正。`NOTIFICATION_TIMEOUT` はリトライを消費せずそのまま再購読へ進むようにした。subscriber が timeout 時に非ゼロ終了することがあるため、終了コードより先に stdout の JSON で idle timeout を判定する（#111 の手動検証で発見）
- レビュー起動結果ダイアログの標準出力・標準エラーが文字化けする不具合を修正。GUI プロセスにはコンソールが無く、リダイレクトした子プロセス出力の既定エンコーディングが UTF-8 に解決されないため、`ReviewLauncherService` と `McpSubscriptionService` の `ProcessStartInfo` に `StandardOutputEncoding` / `StandardErrorEncoding = UTF-8` を明示した（#111 の手動検証で発見）
- Settings の Resource URI 複数行 TextBox で、`Enter` 入力による改行が `\r`（WinUI3 TextBox の既定の改行文字）であるため `Split('\n')` では分割されず、複数 URI を手入力すると1要素に `\r` が混入したまま保存されていた不具合を修正。`\r` と `\n` の両方で分割するように変更（#111 の手動検証で発見）

### Added
- レートリミット解除の事前通知予約機能を追加。メインウィンドウに「レートリミット状態」セクションを新設し、「更新」ボタン押下時に Resource URIs 欄に設定された `ratelimit://` スキームの URI を MCP の `resources/read`（`McpResourceProbe.ReadResourceTextAsync`）で読み取り、`{"limits":[{"id","label","resetAt"}]}` 形式の JSON を一覧表示する。各項目の「通知予約」ボタンで解除予定時刻までのメモリ内タイマー（`RateLimitReminderService`、`Task.Delay` ベース）を起動し、満了時にトースト通知で解除を知らせる。既存の常時購読（notification 待ち）方式とは別に、都度取得する軽量な手動更新方式を採用。アプリ終了時にタイマーは破棄され、通知予約は永続化しない（#114）
- Recent review events の各項目に「コマンドをコピー」アクションを追加。「レビュー起動」と同じスロット選択（reviewer/reviewed）・引数展開（`{owner}`/`{repo}`/`{prNumber}`/`{reason}`）ロジックを再利用し、実際に起動せずにフルコマンド文字列をクリップボードへコピーできる。任意のターミナルや既存セッションへ貼り付けて実行する用途を想定し、コピー成功・失敗は InfoBar で軽くフィードバックする（#121）
- PR URL（`https://github.com/{owner}/{repo}/pull/{number}`）または `owner/repo#number` をメインウィンドウに入力すると、mcp-resource-subscriber の `call --tool enqueue_review` 経由で thread-owl の review queue へ enqueue し、既存の購読 → 通知 → ランチャーの正規経路でレビューサイクルを手動開始できる機能を追加。queue をバイパスして直接ランチャーを起動する経路は作らない。reason（`opened` / `synchronized` / `re-review-requested`、既定 `opened`）を選択でき、enqueue 失敗はツールエラー（allowlist 外等）・認証エラー・通信エラーを exit code で区別してユーザーに通知する。購読が停止中に登録した場合は通知が届かない旨も案内する。レビュー対応として、購読側と同じ固定引数（`SubscriberArguments`）を `call` モードにも引き継ぐようにし、`call` サブコマンドが存在しない mcp-resource-subscriber v0.3.0 以下を `--version` の事前確認で検出して案内するようにし、認証エラー（exit code 2）は `errorCode` に応じて `AUTH_FAILED`（`MCP_PROBE_AUTH_TOKEN` 不備等、`--login` では解消しない）と `AUTH_LOGIN_REQUIRED` 等（`--login` で解消する）を区別して案内するように改善（#122）
- ランチャー設定を reviewer-side / reviewed-side の 2 スロット化。Settings に「起動ロール」（reviewer / reviewed）の RadioButton と、各スロットの Path・Arguments 入力欄を追加。`ReviewEvent.Source` が `queue://review/re-review-requests` の場合は常に reviewer スロットを使用し、`queue://review/queue` の場合は LauncherRole 設定で切り替える（#91）
- `LauncherArgumentBuilder` に `{reason}` プレースホルダーを追加。`reviewEvent.Reason`（`opened` / `synchronized` / `re-review-requested` 等）を引数テンプレートに埋め込めるようになった。値は既存の `_safeNameRegex` で検証する（#91）
- デフォルトのランチャー設定を `review-raven` から `claude -p "/thread-owl-pr-reviewer ..."` / `claude -p "/thread-owl-review-cycle ..."` に更新（#91）
- 旧単一スロット設定（`LauncherCommandPath`）をユーザーがカスタマイズしていた場合、`SettingsService` コンストラクタ起動時に reviewer スロットへ自動移行（#91）
- Settings の「Resource URI」欄に「MCP から取得」ボタンを追加。`ModelContextProtocol.Core` v1.4.0（公式 C# MCP SDK）を使用し、MCP Streamable HTTP transport（initialize handshake → Mcp-Session-Id セッション管理 → resources/list）を通じて mcp-gateway から Resource URI を動的に取得できるようになった。`MCP_PROBE_AUTH_TOKEN` 環境変数が設定されている場合は Bearer トークンを全リクエストに付与する（#103）
- Gateway URL の「コンテナから自動設定」で、検出したポートに加えて MCP route パス（既定 `/mcp/thread-owl`）を選択・入力できるダイアログを追加。`docker ps` から取得した `http://localhost:PORT` に route を結合して設定するため、gateway root（`/`）に繋いで `resources/list` が 404 になる問題を回避できる。Gateway URL 入力欄にも route を含むプレースホルダーを表示（#102）
- Settings の「Resource URI」欄を複数行 TextBox（`ResourceUrisBox`）に変更し、1 行 1 URI で複数の URI を設定できるようになった。`AppSettings.ResourceUris`（`List<string>`）に保存し、旧単一フィールド `ResourceUri` からの自動移行も対応（#92）
- 複数 Resource URI に対して並行購読ループを実行。`McpSubscriptionService` が `Task.WhenAll` を使って各 URI の購読ループを独立したタスクとして並行実行し、`_activeProcesses`（`ConcurrentDictionary`）で全プロセスを追跡する。`StopAsync()` および `DisposeAsync()` が全プロセスをキルして確実に停止する（#92）

## [0.1.3] - 2026-06-27

### Added
- 自動起動（タスクスケジューラ）が未登録の場合に、メインウィンドウ上部に設定画面への誘導を促すオンボーディング案内（InfoBar）を表示する機能を追加（#83）

### Fixed
- mcp-gateway のエンドポイントが 404 を返した際や、接続エラーが発生した際のエラーメッセージをパースし、設定画面への確認を促す分かりやすいメッセージに変換するよう改善。また、ログに `[CONN_REFUSED]`、`[HTTP_404]`、`[AUTH_ERROR]` などのエラータグを出力するようにした（#85）
- `--tray`（トレイ）モードで起動した際、WinUI 3 (Windows App SDK) の仕様で `Window.Activate` を呼ばないとメッセージループが開始せずプロセスが即時終了してしまう問題を修正（#84）
- Settings の「自動起動」トグルで `schtasks.exe /SC ONLOGON` が UAC 有効環境で Access denied になる問題を修正。`Register-ScheduledTask` / `Unregister-ScheduledTask` PowerShell コマンドレット（CIM API 経由）に切り替え、非昇格プロセスでも登録・削除できるようにした（#86）

### Changed
- 「自動起動」トグルのオン・オフ時に確認ダイアログを表示するようにした。「いいえ」を選択するとトグルが元の状態に戻り、操作を中断できる（#86）
- `mcp-resource-subscriber` コマンドの自動検索に `pnpm bin -g` によるグローバル bin ディレクトリのフォールバックを追加。PATH に含まれない環境でも自動解決できるようになった（#88）
- `SettingsService.ResolveCommandPath` に重複していた `McpSubscriptionService` 内の同名メソッドを統合（#88）

## [0.1.2] - 2026-06-21

### Fixed
- MSI: `schtasks /SC ONLOGON` が非昇格環境（UAC 有効）で Access denied になる場合にインストールがロールバックされていた問題を修正。`RegisterScheduledTask` カスタムアクションを `Return="ignore"` に変更し、タスク登録失敗でもインストールを続行するようにした。タスク登録はインストール後にアプリ内 UI またはセットアップ Zip の `install.cmd` / `install.ps1` から行う（#79）
- MSI: `[SystemFolder]` は 32-bit コンテキストで `SysWOW64` に解決されるため `[System64Folder]` に変更し 64-bit `schtasks.exe` を明示（Register・Unregister 両アクション）

### Added
- `scripts/install.cmd`・`scripts/uninstall.cmd`・`scripts/create-shortcuts.cmd` を追加。PowerShell の ExecutionPolicy に関わらずダブルクリックで実行可能な `.cmd` ラッパー（#79）
- セットアップ Zip（`SquirrelNotifier-Setup-*.zip`）に `.cmd` ラッパーを同梱

### Removed
- リリース成果物から `SquirrelNotifier-WinUI3-*.zip`（バイナリのみ zip）を廃止。`SquirrelNotifier-Setup-*.zip`（バイナリ＋スクリプト同梱）に一本化

## [0.1.1] - 2026-06-21

### Fixed
- MSI インストール時に `WixQuietExec` が `CustomActionData` を取得できず「Setup Wizard ended prematurely」でロールバックされる問題を修正。`SetProperty Id` をカスタムアクション名と同一にする必要があるため `RegisterScheduledTaskCmdLine` → `RegisterScheduledTask`、`UnregisterScheduledTaskCmdLine` → `UnregisterScheduledTask` に変更（#74, PR #75）

### Documentation
- README に `mcp-resource-subscriber` および `mcp-gateway` の前提ツール・セットアップ手順セクションを追加（#73, PR #76）

## [0.1.0] - 2026-06-21

### Added
- WiX 7 SDK 形式 MSI インストーラー（`SquirrelNotifier.Installer`）を追加。`%LocalAppData%\Programs\SquirrelNotifier\` への per-user インストール、スタートメニューショートカット、インストール時のタスクスケジューラ自動登録・アンインストール時の自動削除に対応
- `scripts/create-cert.ps1` を追加。ローカルでのテスト署名用に自己署名 CA 証明書とコード署名 PFX を生成するスクリプト（管理者権限必須、本番配布には商用証明書を使用すること）
- リリース CI (`release.yml`) に WiX 7 インストールおよび MSI ビルドステップを追加。リリース成果物として `SquirrelNotifier-Setup-<version>-<platform>.msi` を追加
- アプリ内から Windows タスクスケジューラへの自動起動タスク登録・解除・修復が可能になった。設定パネルに「自動起動」トグルとタスク状態表示・修復ボタンを追加（`ITaskSchedulerService` / `TaskSchedulerService`）
- エラー状態（`SubscriptionState.Error`）時にトレイアイコンを警告マーク付きアイコン（`squirrel-notifier-error.ico`）に自動で差し替え、エラー内容をツールチップに表示するように変更
- エラー状態への初回遷移時にトレイバルーン通知（Windows 標準 `NIF_INFO` / `NIIF_WARNING`）でエラーメッセージを通知する機能を追加。エラーが解消されて再度エラー状態になった場合は再通知する
- `TrayIconService` に `UpdateIcon(string iconFileName)`（アイコン差し替え）および `ShowBalloonTip(string title, string text)`（バルーン通知）メソッドを追加
- 通知の重複排除キャッシュおよび直近イベント履歴をアプリ終了をまたいで永続化する機能を実装（`CacheService` / `ICacheService`）。起動時に `Cache restored` ログを出力し、前回セッションの既読イベント ID を自動復元するよう変更
- `App.xaml.cs`: self-contained モードで `Microsoft.WindowsAppRuntime.Insights.Resource.dll` が見つからない場合でもクラッシュせず起動できるよう `AppNotificationManager.Default.Register()` に例外ハンドリングを追加
- 起動時自動チェック等において、特定のバージョンをスキップするための「スキップされたバージョン」保存機能 (`LastSkippedVersion`) を `AppSettings`/`SettingsService` に追加
- 自動更新通知のダイアログに「このバージョンをスキップ」ボタンを追加し、手動チェックと自動チェックの切り分け・スキップ動作の永続化をサポート
- `AutoUpdateService.CheckForUpdatesAsync` に、一時的なネットワーク障害に対応するための自動リトライ機能（最大3回、指数バックオフ）および個別リクエストのタイムアウト処理（5秒）を導入
- ユーザー起点の安全な外部 launcher (`ReviewLauncherService`) を実装。通知またはアプリ UI から、設定された外部レビューアクションを安全に起動可能に
- 安全な引数置換機構 (`LauncherArgumentBuilder`) を導入。プレースホルダー (`{owner}`, `{repo}`, `{prNumber}`, `{prUrl}`) を用いて、シェルインジェクションを完全に防ぎつつ安全に引数を構築
- 設定ウィンドウに外部 launcher 用の設定項目 (Launcher Path, Launcher Args, Launcher Timeout) を追加
- トースト通知に「レビューを起動」ボタンを追加し、通知から即座にレビュー実行をトリガー可能に
- レビュー実行中のステータス表示、ユーザーからのキャンセル、実行完了後の詳細な結果 (終了コード、stdout/stderr) を表示するダイアログ UI を MainWindow に追加
- MCP 購読ブリッジ機構 (`McpSubscriptionService`) を新設。`mcp-resource-subscriber` と連携し、外部 MCP リソースからのレビュー更新イベントを取得可能に
- `McpSubscriptionService` 用の動作確認 UI を設定ウィンドウに追加（コマンド実行パス、引数、URL、リソース URI、タイムアウト of 編集に対応）
- `mcp-resource-subscriber` から受信した JSON 形式のレビューイベント（`ReviewEvent`）をパースするデシリアライズ・検証機構を導入
- レビューイベント専用の Windows 通知を構築（通知から検証済み PR URL をブラウザで開くアクション、およびアプリを開くアクションに対応）
- メモリ内の重複排除キャッシュ（最大100件）による、同一 `eventId` を持つレビューイベントの重複通知防止機能を実装
- メイン画面 UI に直近のレビューイベント（Recent review events）の履歴リストを追加し、一覧から直接 PR を開けるボタンを配置

### Removed
- 旧 WSL カーネル監視機能 (`KernelWatcherService` および関連 UI / ロジック) を完全に廃止

### Fixed
- `RestoreCacheAsync`: キャッシュ復元時に seen IDs / recent events の上限トリミングが行われず、100件制限が崩れる問題を修正
- `CacheService.LoadAsync`: デシリアライズ結果の `SeenEventIds` / `RecentEvents` が null の場合に `RestoreCacheAsync` の `foreach` で例外が発生しキャッシュ全体が無視される問題を修正。null コレクションを空リストに正規化するよう変更
- `CacheService.SaveAsync`: 例外を内部で握り潰していたためキャッシュ書き込み失敗の原因がトラブルシュートできなかった問題を修正。例外を上位に伝播し、`PersistCacheAsync` 側で警告ログ出力するよう設計を統一
- `App.xaml.cs`: `CacheService` のフィールド初期化時に `Directory.CreateDirectory` 等の I/O 例外が発生するとアプリが起動前にクラッシュする問題を修正。`try/catch` に変更し失敗時はキャッシュなしで起動継続するよう変更
- `App.xaml.cs`: `AppNotificationManager.Default.Register()` が失敗した状態で `_notificationService.Initialize()` を呼び出すと再び `COMException` が発生し起動クラッシュする可能性を修正。`Register()` 成功フラグで通知サービス初期化をスキップするよう変更
- `install.ps1` のパス解決を修正し、`publish/` 配下の自己完結型成果物のみを検索・登録するよう変更（`bin/` 直下の非自己完結型 EXE が誤って選択される問題を解消）
- MCP 購読接続の一時的なエラー（サーバー未起動や認証切れによる `fetch failed` 等）発生時に、即座に `Error` 状態で停止していた問題を修正。指数バックオフ（初期 1 秒、最大 32 秒、最大 5 回）による自動リトライを導入し、一時的な障害からの自動回復を可能にした
- リトライバックオフ待機中に `StopAsync` を呼び出しても遅延が終わるまで戻らない問題を修正。待機トークンを `processToken` に変更し、停止操作に即応答するよう改善
- `OperationCanceledException` を無条件に正常停止として扱うため、停止操作と無関係な OCE でも `State` が `Running` のまま通知が届かなくなる問題を修正。`token`・`_activeProcessCts` のキャンセル時のみ正常停止、それ以外はリトライ/エラーへ流すよう条件を修正

### Changed
- `.csproj` に `<WindowsPackageType>None</WindowsPackageType>` を追加（WinUI3 unpackaged self-contained アプリとして正しく起動するための標準設定）
- `ReviewEventParser.Parse` の戻り値を `List<ReviewEvent>` に変更し、`ReviewCandidate` 配列内の全新着イベントをパース・ループ処理するよう拡張
- `ReviewCandidate` ペイロードの変更（`url` 等の欠落）に合わせ、`Owner`/`Repo`/`PrNumber` からの PR URL 自動構築と、`queuedAt` 等による `eventId` 自動生成を追加、および `sourceCommentId` の型を `long?` に修正しデシリアライズエラーを防止
- 重複排除とトースト通知の安定動作を検証するユニットテストを `ReviewEventParserTests` 等に新設・追従
- アプリケーション、ソリューション、プロジェクト、アセンブリ名、および C# の名前空間を `SquirrelNotifier` へ統一
- 設定保存先ディレクトリを `%LocalAppData%\WSLKernelWatcher` から `%LocalAppData%\SquirrelNotifier` に変更
- ウィンドウタイトル、トレイアイコンのツールチップ、メニュー、アプリ表示名を `Squirrel Notifier` へ変更
- 自動更新用の GitHub リポジトリ URL および User-Agent を `squirrel-notifier` に追従
- タスクスケジューラのタスク登録（`install.ps1`）を `Squirrel Notifier` に変更し、旧タスク検出時に手動移行手順を促すよう改善
- タスクスケジューラのタスク登録スクリプト (`install.ps1`) の説明文を MCP リソース監視のものに更新
- アプリケーションのアイコンファイルを `squirrel-notifier.ico` (新アセット `assets/squirrel-notifier.png` を基に生成) に差し替え
- 開発用ツールセットの Python プロジェクト名を `squirrel-notifier-devtools` に変更
- トレイ通知のイベント発生時、レビュー URL 開くボタンを（今回のスコープ外のため）一旦削除

[Unreleased]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.5.2...HEAD
[0.5.2]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.1.3...v0.2.0
[0.1.3]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/scottlz0310/squirrel-notifier/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/scottlz0310/squirrel-notifier/releases/tag/v0.1.0
