# エージェント実行ライブログウィンドウ（#144）

エージェント実行セッション（#143 の `AgentExecutionSession`）の進捗とログをリアルタイム表示する小型サブウィンドウ。表示ロジックは `ViewModels/AgentExecutionViewModel` に分離されており、Window / XAML コードビハインドは薄い接続層に留める。

## 表示仕様

- stdout / stderr / progress をログ行として視覚的に区別して表示する
- phase（#143 contract の 0 始まり index / total / label）を ProgressBar と状態テキストで表示する。progress event を出力しないランチャーは実行終了まで indeterminate（不確定）表示になる
- 成功・失敗・キャンセル・タイムアウトの terminal 状態を区別して表示する
- 成功終了時は短い猶予の後に自動クローズする（Settings の `LiveLogAutoCloseEnabled` で無効化可能）。失敗・キャンセル・タイムアウト時は診断のため保持する
- ログは行数上限付きの rolling buffer（`AgentExecutionViewModel.MaxLogLines` = 1000 行）で管理し、長時間実行でも UI メモリが無制限に増加しない

## ウィンドウの挙動（`AgentExecutionWindow`）

- 通知またはイベント行からエージェントを起動した時点で表示され、現在モニターの work area 内・右下へ DPI を考慮して配置される（`WindowPlacementCalculator`）
- 最前面ピン留めトグル（`OverlappedPresenter.IsAlwaysOnTop`）を提供する
- セッションのイベントは DispatcherQueue へ集約反映（coalescing バッチ化）され、UI 更新頻度が抑制される
- 「キャンセル」ボタン、および実行中のウィンドウクローズで実行をキャンセルする（バックグラウンド継続はしない）
- 同時実行抑止（単一実行）は launcher 側で維持されており、ウィンドウも同時に 1 つのみ表示される

## ログのサニタイズ

表示前に各行へ以下を適用する（`Helpers/AnsiControlSanitizer`）:

- ANSI エスケープシーケンス（CSI / OSC / Fe）の除去
- 水平タブ（`\t`）を除く C0 制御文字と DEL の除去

## 機密値マスキング

表示前に各行へマスキング（`Helpers/SecretMasker`、`***` へ置換）を適用する。**マスキング対象は以下の 2 種類に明示的に限定する**:

### (a) 既知トークン形式のパターン

| 対象 | パターン概要 |
|---|---|
| GitHub classic / OAuth / installation トークン | `ghp_` / `gho_` / `ghu_` / `ghs_` / `ghr_` + 英数字 16 文字以上 |
| GitHub fine-grained PAT | `github_pat_` + 英数字・アンダースコア 22 文字以上 |
| sk- 形式 API キー（OpenAI / Anthropic 等） | `sk-` + 英数字・記号 16 文字以上（`sk-ant-` / `sk-proj-` を包含） |
| Authorization ヘッダ | `Bearer <トークン>` のトークン部 |

### (b) squirrel-notifier 自身が参照する認証情報

- 環境変数 `MCP_PROBE_AUTH_TOKEN` の値と一致する文字列

### 保護対象外（重要）

任意テキストからの機密値検出は原理的に不可能なため、**上記ルールに合致しない機密値は保護対象外**である。例:

- 上記以外の形式の独自トークン・パスワード・接続文字列
- 改行やエスケープで分断されたトークン
- エージェントが出力する、squirrel-notifier が関知しない認証情報

エージェントに渡すプロンプトやスキル定義の側で、機密値を stdout に出力しない運用を優先すること。
