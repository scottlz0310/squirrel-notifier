# `MainWindow.xaml.cs` 残存責務の棚卸し

## 1. 調査範囲と判定基準

調査時点は 2026-09-12、`main` の HEAD は
`cb2810811d1f3868a3f80e1f2cc914eeec5f143e` である。
対象は `winui3/SquirrelNotifier.WinUI3/MainWindow.xaml.cs` と、同ファイルが直接参照する
既存の `Services/`・`Helpers/`・`Models/`・テストである。

同ファイルの物理行数は 1,531 行で、`scripts/check-code-behind-size.ps1` の上限も
1,531 行である。終了時のイベント解除を明示する UI 配線を追加したため、前回の 1,521 行から増えている。
これは抽出結果と無意識の肥大化を観測するための指標であり、
本棚卸しの完了条件や、後続作業の削減目標ではない。

判定は次の基準で行う。

| 判定 | 意味 |
| --- | --- |
| `維持候補` | イベント受信、サービス呼び出し、UI反映、DispatcherQueue、Dialog/Window生成に限定され、現状の境界を維持できる |
| `境界確認` | UIランタイム上の処理として残せるが、判断・状態・メッセージ組み立てが混在していないかを後続設計で確定する |
| `抽出候補` | 判断、状態、I/O、プロセス起動、クリップボード、永続化、またはドメイン文言の規則を持ち、`Helpers/`・`Services/`等へ移す候補である |

行数とカバレッジは、責務を切り出せた結果を確認するために記録する。
数値だけを合わせるために処理を分割したり、`*.xaml.cs` の除外を変更したりしない。

## 2. 現状の構成

### 2.1 フィールドと静的データ

| 行 | 項目 | 判定 | 現状と後続方針 |
| ---: | --- | --- | --- |
| 27 | `_isExitRequested` | 抽出済み | 終了要求の状態と冪等性は `WindowLifecycleCoordinator` が所有する |
| 28 | `_service` | 維持候補 | 購読サービスのDI依存。UIからの開始・停止呼び出しに使用 |
| 29 | `_loggingService` | 境界確認 | DI依存自体は妥当。UI固有のログ文言・例外処理を残す理由を経路ごとに確認 |
| 30 | `_settingsService` | 境界確認 | Coordinator経由へ寄せられているが、一部イベントから直接更新している |
| 31 | `_updateCheckCoordinator` | 維持候補 | 更新判定とスキップ状態は抽出済み。UIはダイアログ表示と結果変換を担当 |
| 32 | `_logEntries` | 維持候補 | `ObservableCollection` のUI反映用。上限・追従判断は別責務として抽出候補 |
| 33 | `_reviewEvents` | 境界確認 | UI一覧用。上限、削除、終了状態との連携はCoordinator候補 |
| 34 | `_trayIconService` | 維持候補 | トレイUIのサービス依存 |
| 35 | `_hwnd` | 維持候補 | Window/Win32境界のハンドル |
| 36 | `_isInitializing` | 抽出候補 | 設定イベント抑止の状態。初期化と入力反映の境界をCoordinatorまたはViewModelで管理する |
| 37 | `_notificationService` | 維持候補 | 通知イベントの購読依存 |
| 38 | `_launcherService` | 境界確認 | 起動・キャンセル・コマンド生成を利用。コマンド生成は既存Serviceに委譲済み |
| 39 | `_urlOpener` | 維持候補 | URL起動のサービス依存 |
| 40 | `_fileOpener` | 維持候補 | ファイル・フォルダー起動のサービス依存 |
| 41 | `_clipboardService` | 維持候補 | クリップボード設定のサービス依存 |
| 42 | `_windowIconService` | 維持候補 | ウィンドウアイコン設定のサービス依存 |
| 43 | `_taskSchedulerService` | 境界確認 | `AutoStartCoordinator` の構築に使用。構成ルートを`App`へ寄せるか後続で判断 |
| 44 | `_reviewRegistrationService` | 維持候補 | レビュー登録のサービス依存 |
| 45 | `_reviewEventCleanupCoordinator` | 維持候補 | イベント保持・終了確認のCoordinator依存 |
| 46 | `_rateLimitReminderService` | 境界確認 | リマインダーの操作依存。選択・状態反映の境界を確認 |
| 47 | `_rateLimitSnapshotService` | 境界確認 | MainWindow内で生成されるService。構成ルートで生成する案を検討 |
| 47 | `_autoPauseGate` | 境界確認 | 複数経路とライブログウィンドウで共有する状態。所有者を維持する理由を記録 |
| 48 | `_subscriptionStateCoordinator` | 維持候補 | 購読状態の表示判断とエラー通知の一回制御を管理するCoordinator |
| 49 | `_reviewStartCoordinator` | 境界確認 | MainWindow内で生成されるCoordinator。生成責務とUI依存の分離を検討 |
| 50 | `_rateLimitRefreshCoordinator` | 境界確認 | MainWindow内で生成されるCoordinator。構成ルートへの移動を検討 |
| 51 | `_settingsCoordinator` | 境界確認 | MainWindow内で生成されるCoordinator。設定UIとの境界を維持しつつ構成を検討 |
| 52 | `_launcherPresetCoordinator` | 維持候補 | プリセット適用・選択同期の状態を保持するCoordinator |
| 53 | `_autoStartCoordinator` | 維持候補 | 自動起動の判定・I/Oは抽出済み。UIは結果反映を担当 |
| 54 | `_gatewayLoginCoordinator` | 維持候補 | ログイン開始可否と結果解釈は抽出済み。UI進行状態は後続で確認 |
| 55 | `_rateLimits` | 維持候補 | レートリミット一覧のUI反映用 |
| 56 | `_rateLimitAgentOptions` | 境界確認 | UI選択状態。設定更新とリマインダー操作を分離する |
| 57 | `_logListScrollViewer` | 境界確認 | Visual Tree探索結果のキャッシュ。UIアダプターとして残すか、キャッシュ状態を移すか判断 |
| 61 | `_reviewNotificationContent` | 維持候補 | トレイポップアップのUIコンテンツ |
| 62 | `_isTrayPopupAvailable` | 境界確認 | UI能力状態。`TrayIconService` が所有できるか確認 |
| 65 | `_agentExecutionWindow` | 境界確認 | Windowラッパーを保持するライフサイクル状態。保持理由は妥当だが所有者を後続で確認 |
| 40 | `_copyFeedbackCoordinator` | 維持候補 | コピー通知の文言・表示期限・キャンセルを管理するCoordinator。MainWindowはInfoBar反映だけを担当 |
| 674 | `_knownResourceUris` | 抽出候補 | ドメイン上の既知URI一覧。`Models/`または設定用Helperへ移しテスト可能にする |
| 680 | `_enqueueReviewReasons` | 抽出候補 | 登録理由の許容値。登録用Model/Helperへ移し、入力検証と共有する |

### 2.2 Win32境界・コンストラクター

| 行 | 項目 | 判定 | 現状と後続方針 |
| ---: | --- | --- | --- |
| 67 | `ShowWindow` | 維持候補 | Windowの表示・非表示というUI境界。必要ならWindowPresenterへ委譲 |
| 70 | `LoadImage` | 抽出候補 | ファイルからアイコンを読むI/O。`WindowIconService` 等へ移し、失敗方針をテストする |
| 73 | `SendMessage` | 維持候補 | Windowアイコン設定のWin32境界。アイコンService側で所有する |
| 76-82 | `_swHide`、`_swShow`、`_wmSetIcon`、`_iconSmall`、`_iconBig`、`_imageIcon`、`_lrLoadFromFile` | 抽出候補 | アイコンServiceへ移す定数。表示・非表示の定数とは分離する |
| 83-223 | `MainWindow` | 境界確認 | UI初期化に加え、複数のService/Coordinatorを生成しイベントを配線している。構成ルートを`App`へ寄せる案を後続で検討する |

## 3. メソッド棚卸し

### 3.1 ウィンドウ・購読・トレイ（224-442行）

| 行範囲 | メソッド | 判定 | 推奨する受け皿・理由 |
| ---: | --- | --- | --- |
| 224-229 | `OnPaneViewportSizeChanged` | 維持候補 | `MainWindowLayout` を呼び、UIプロパティへ反映するだけ |
| 230-235 | `OnGoToSettingsClick` | 維持候補 | Expanderの展開とフォーカス移動というUI操作 |
| 236-242 | `ShowWindowFromTray` | 境界確認 | Window表示とイベント更新をまとめている。Windowライフサイクル境界を後続で確認 |
| 243-247 | `HideWindowToTray` | 維持候補 | Windowの非表示だけを行う |
| 248-274（旧） | `SetWindowIcon` | 抽出済み | `WindowIconService` へ移し、アイコンパス、存在確認、Win32 呼び出し、失敗結果を code-behind から除去 |
| 275-279 | `OnStartClick` | 維持候補 | 購読Serviceの開始を呼ぶだけ |
| 280-284 | `OnStopClick` | 維持候補 | 購読Serviceの停止を呼ぶだけ |
| 285-289 | `OnRetryClick` | 維持候補 | 購読Serviceの再開始を呼ぶだけ |
| 290-294 | `OnExit` | 維持候補 | トレイへ隠すUI操作 |
| 295-312 | `OnTrayIconLoaded` | 維持候補 | `Loaded` 後にポップアップを接続する#229のUIランタイム境界 |
| 313-327 | `AttachTrayPopup` | 境界確認 | H.NotifyIconのUI初期化は残す必要があるが、失敗ログと能力状態はService側候補 |
| 328-332 | `OnTrayOpenCommandExecuteRequested` | 維持候補 | トレイメニューからWindowを表示する配線 |
| 315-321 | `OnTrayRightClickCommandExecuteRequested` | 抽出済み | `TrayContextMenu.Show` と `TrayMenuLayout.Build` を UI 境界に残し、選択後の振り分けを `TrayCommandCoordinator` へ委譲 |
| 323-340 | `ExitApplication` | 抽出済み | 終了要求の状態、イベント解除、UI所有リソース破棄、`Close()` の順序を `WindowLifecycleCoordinator` へ移した |
| 377-381 | `OnStatusTextChanged` | 維持候補 | DispatcherQueue経由のText反映 |
| 360-369 | `OnStateChanged` | 維持候補 | 購読状態を UI スレッドへ配送し、`SubscriptionStateCoordinator` の結果を反映する配線 |
| 371-400 | `ApplySubscriptionStatePresentation` | 維持候補 | Coordinator が決めたログ・トレイ・InfoBar の値を UI へ反映する境界 |
| 431-442 | `UpdateControls` | 維持候補 | `SubscriptionControlAvailability` の結果をUIへ反映する |

### 3.2 ログ・設定・Resource・レートリミット（443-863行）

| 行範囲 | メソッド | 判定 | 推奨する受け皿・理由 |
| ---: | --- | --- | --- |
| 443-469 | `OnLogAppended` | 抽出候補 | 表示上限200行、削除、末尾追従を一つのイベント処理に含む |
| 470-482 | `ShouldFollowLogTail` | 境界確認 | 判定は`LogFollowPolicy`へ抽出済み。ScrollViewer取得との境界だけを残す |
| 483-493 | `ResolveLogListScrollViewer` | 境界確認 | Visual Treeとキャッシュを扱うUIアダプター候補 |
| 494-514 | `FindDescendantScrollViewer` | 維持候補 | Visual Treeを探索するUI補助。テスト可能性が必要ならUIアダプターへ移す |
| 520-523 | `OnOpenLogFolder` | 抽出済み | #289 で `IFileOpener` へ委譲し、フォルダー起動の直接 I/O と例外処理を code-behind から除去 |
| 531-540 | `OnSettingChanged` | 境界確認 | 初期化・プリセット適用の抑止と設定保存を判断している |
| 541-550 | `OnLiveLogAutoCloseToggled` | 境界確認 | UI値から設定Serviceを直接更新。入力反映境界を`SettingsCoordinator`と整理 |
| 551-560 | `OnAutoReviewStartToggled` | 境界確認 | UI値から設定Serviceを直接更新。上記と同じ境界 |
| 566-575 | `OnReviewerPresetSelectionChanged` | 維持候補 | Coordinatorへ適用を委譲し、UIイベントを接続する |
| 576-585 | `OnReviewedPresetSelectionChanged` | 維持候補 | Coordinatorへ適用を委譲し、UIイベントを接続する |
| 586-609 | `ApplyLauncherPreset` | 維持候補 | Coordinatorの結果をTextBoxへ反映し、保存を呼ぶ |
| 610-617 | `UpdateLauncherPresetComboBoxSelection` | 維持候補 | Coordinatorの選択同期をUIへ接続する |
| 618-661 | `OnAutoDetectGatewayUrlClick` | 境界確認 | 検出結果の表示とroute選択Dialogを構築する。検出はCoordinatorへ委譲済み |
| 662-672 | `ShowAlertDialogAsync` | 維持候補 | ContentDialog生成・表示のみ |
| 687-707 | `OnSelectResourceUriClick` | 境界確認 | 既知URI選択Dialogと入力マージ。既知URI一覧の所有者は移動候補 |
| 708-742 | `OnFetchResourceUriFromMcpClick` | 境界確認 | 取得はCoordinatorへ委譲済み。選択Dialogと入力マージを担当 |
| 743-773 | `OnRefreshRateLimitClick` | 維持候補 | 更新判断はCoordinatorへ委譲し、一覧・InfoBarへ結果を反映する |
| 774-784 | `OnRateLimitAgentOptionChanged` | 抽出候補 | 監視対象の収集と設定永続化をイベント内で判断している |
| 785-799 | `OnRateLimitReminderFired` | 維持候補 | ReminderイベントをUI一覧へ反映する |
| 800-818 | `OnToggleRateLimitReminderClick` | 抽出候補 | Cancel/Scheduleの分岐とUI状態更新を直接所有している |
| 819-828 | `OnTimeoutChanged` | 維持候補 | UI入力変更から設定保存を呼ぶ配線 |
| 829-852 | `SaveCurrentSettings` | 境界確認 | 複数のUI入力を`SettingsInput`へ変換する境界。変換規則はCoordinator/Helper側で維持する |
| 853-863 | `OnAppWindowClosing` | 維持候補 | `WindowLifecycleCoordinator` の終了要求状態を参照し、通常の閉じる要求をトレイ非表示へ変換するWindowイベント |

### 3.3 更新・通知・レビュー（864-1284行）

| 行範囲 | メソッド | 判定 | 推奨する受け皿・理由 |
| ---: | --- | --- | --- |
| 864-866 | `CheckForUpdatesAsync` | 維持候補 | `UpdateCheckCoordinator` への委譲 |
| 867-886 | `ShowUpdateDialogAsync` | 維持候補 | UpdatePresentationをDialogとActionへ変換するUI境界 |
| 887-907 | `TryOpenUrl` | 抽出済み | #286の初回実装で削除し、URL起動を既存`IUrlOpener`へ委譲した |
| 908-912 | `OnOpenStatuslineDocsClick` | 維持候補 | 固定URLを既存`IUrlOpener`へ渡すUIイベント |
| 913-924 | `OnReviewEventReceived` | 維持候補 | UIスレッドへ配送するDispatcherQueue境界 |
| 925-956 | `HandleReviewEvent` | 抽出候補 | 一覧上限、削除追跡、Action可否、自動起動、通知の順序を判断している |
| 957-979 | `ShowReviewNotification` | 境界確認 | ポップアップとバルーンのフォールバック境界。UI所有の理由を明文化する |
| 926-933 | `ShowReviewBalloon` | 境界確認 | 通知文言は `ReviewNotificationFormatter` へ委譲し、ここにはトレイ通知の表示だけを残す |
| 991-996 | `OnNotificationRequested` | 維持候補 | 通知モデルをトレイ通知へ反映する配線 |
| 997-1005 | `OnDismissEventClick` | 境界確認 | UI一覧の削除とCleanup Coordinatorの追跡解除を接続する |
| 1006-1023 | `OnReviewEventsRemoved` | 維持候補 | Coordinatorの削除結果をUI一覧へ反映する |
| 1024-1045 | `OnOpenPrClick` | 境界確認 | 安全なGitHub URL判定後の起動を`IUrlOpener`へ委譲。URLの直接I/Oは#286の初回実装で除去した |
| 1046-1057 | `OnTrayPopupOpenPrRequested` | 境界確認 | ポップアップを閉じ、安全なGitHub URL判定後の起動を`IUrlOpener`へ委譲 |
| 1058-1064 | `OnTrayPopupLaunchReviewRequested` | 維持候補 | ポップアップ終了、Window表示、既存レビュー起動の配線 |
| 1065-1070 | `OnTrayPopupOpenAppRequested` | 維持候補 | ポップアップ終了とWindow表示の配線 |
| 1071-1075 | `OnTrayPopupDismissRequested` | 維持候補 | ポップアップを閉じるだけの配線 |
| 1076-1083 | `OnLaunchReviewerClick` | 維持候補 | 既存のレビュー起動へ委譲するUIイベント |
| 1084-1091 | `OnLaunchReviewedClick` | 維持候補 | 既存のレビュー起動へ委譲するUIイベント |
| 1092-1099 | `OnCopyReviewerCommandClick` | 維持候補 | 対象イベントとroleをコピー処理へ渡す配線 |
| 1100-1107 | `OnCopyReviewedCommandClick` | 維持候補 | 対象イベントとroleをコピー処理へ渡す配線 |
| 1043-1057 | `CopyLaunchCommand` | 境界確認 | コマンド生成とClipboard操作は既存Serviceへ委譲し、コピー通知の文言・期限・キャンセルは`CopyFeedbackCoordinator`へ委譲 |
| 1059-1066 | `ShowCopyFeedback` | 維持候補 | `CopyFeedbackPresentation`をInfoBarのSeverity・Message・IsOpenへ反映するUI境界 |
| 1068-1074 | `OnCopyFeedbackExpired` | 維持候補 | Coordinatorの期限切れをDispatcherQueue経由でInfoBarへ反映し、配送不能時だけログへ委譲 |
| 1076-1079 | `OnCopyFeedbackExpirationFailed` | 維持候補 | Coordinatorが整形した失敗メッセージをログServiceへ委譲 |
| 1165-1200 | `ExecuteReviewAsync` | 境界確認 | 起動可否はCoordinatorへ抽出済み。結果のUI表示分岐をPresentationへ移すか確認する |
| 1201-1212 | `ShowReviewStartErrorDialogAsync` | 維持候補 | ContentDialog生成・表示のみ |
| 1218-1243 | `ShowAgentExecutionWindow` | 境界確認 | Window生成と参照保持。WindowライフサイクルのUI境界として残す理由を記録する |
| 1244-1260 | `ConfirmAutoPauseOverrideAsync` | 維持候補 | 確認Dialogの生成・表示と結果変換 |
| 1261-1271 | `UpdateAutoPauseInfoBar` | 維持候補 | Formatterの結果をInfoBarへ反映する |
| 1272-1284 | `UpdateAutoPauseNotApplicableInfoBar` | 境界確認 | 設定Serviceから対象を解決して文言を作る境界。Formatterとの責務を確認 |

### 3.4 登録・ログイン・自動起動（1285-1625行）

| 行範囲 | メソッド | 判定 | 推奨する受け皿・理由 |
| ---: | --- | --- | --- |
| 1285-1339 | `OnEnqueueReviewClick` | 境界確認 | 入力検証、登録結果の分類、ボタン状態、Dialog文言を一つのイベントで扱う |
| 1340-1360 | `ConfirmSubscriptionStartAsync` | 維持候補 | 購読開始確認Dialogの生成・表示 |
| 1361-1367 | `OnLoginToGatewayClick` | 維持候補 | ログイン処理を呼ぶだけのUIイベント |
| 1368-1517 | `StartGatewayLoginAsync` | 抽出候補 | LoginService生成、CTS、進行UI、イベント購読、Dialog lifecycle、完了待ちを混在させている |
| 1518-1537 | `HandleLoginResultAsync` | 境界確認 | 結果解釈はCoordinatorへ委譲済み。InfoBar、購読再開、Dialog表示の境界を確認 |
| 1461-1472 | `CopyToClipboard` | 境界確認 | Clipboard I/O は `IClipboardService` へ委譲済み。成功・失敗の通知は`CopyFeedbackCoordinator`へ委譲し、InfoBar反映だけを行う |
| 1553-1578 | `OnAutoStartToggled` | 境界確認 | Coordinatorへ委譲済み。UI状態反映とDialog表示だけに限定できているか確認 |
| 1579-1592 | `ShowAutoStartConfirmationAsync` | 維持候補 | 確認Dialogの生成・表示 |
| 1593-1611 | `OnRepairAutoStartClick` | 境界確認 | Coordinatorへ修復を委譲し、結果をDialog/UIへ反映する |
| 1612-1614 | `RefreshAutoStartStatusAsync` | 維持候補 | Coordinatorへの委譲 |
| 1615-1625 | `ApplyAutoStartStatus` | 維持候補 | PresentationをUIへ反映する |

## 4. 既存テストとE2Eの対応

`*.xaml.cs` は通常のxUnitカバレッジ対象外であるため、切り出し候補は
ロジックをテスト可能な型へ移したうえで、UI側には結果反映だけを残す。

| 経路 | 既存の検証資産 | 棚卸し上の不足・後続方針 |
| --- | --- | --- |
| レイアウト、購読状態、トレイメニュー、ログ追従 | `MainWindowLayoutTests`、`SubscriptionControlAvailabilityTests`、`TrayMenuLayoutTests`、`LogFollowPolicyTests` | UIイベントの分岐・トレイ選択実行はcode-behindに残るため、抽出時に結果型またはCoordinatorのテストを追加 |
| 設定入力、保存、プリセット、Resource URI | `SettingsInputParserTests`、`SettingsCoordinatorTests`、`SettingsServiceTests`、`LauncherPresetCoordinatorTests` | 直接設定更新、既知URI/理由一覧、Dialog入力の境界を整理し、入力変換をテスト対象にする |
| レートリミットとリマインダー | `RateLimitRefreshCoordinatorTests`、`RateLimitSnapshot*Tests`、`RateLimitReminderServiceTests` | 監視対象の変更とSchedule/Cancel分岐をCoordinatorまたはServiceへ寄せる |
| 自動起動と更新 | `AutoStartCoordinatorTests`、`AutoUpdateServiceTests`、`UpdateCheckCoordinatorTests`、更新GUIの実機E2E | 判定は抽出済み。UIはPresentation反映に限定し、以後の変更でも起動時・手動時のE2Eを維持 |
| Gatewayログイン | `GatewayLoginCoordinatorTests`、`McpLoginServiceTests`、`DeviceLoginOutputParserTests` | Device flowのUI進行、キャンセル、Dialog lifecycleは未分離。UI依存を注入可能な境界へ整理 |
| 購読状態・レビュー登録・起動・通知 | `SubscriptionStateCoordinatorTests`、`ReviewStartCoordinatorTests`、`ReviewEventCleanupCoordinatorTests`、`ReviewNotificationPolicyTests`、`ClipboardServiceTests`、`CopyFeedbackCoordinatorTests` | 購読状態表示の判断・一回通知は抽出済み。イベント一覧上限、通知文言、コピーの表示期限・キャンセル、トレイフォールバックを個別の結果・Policyへ分離 |
| ウィンドウ終了 | `WindowLifecycleCoordinatorTests` | 終了要求の冪等性と、イベント解除・リソース破棄・`Close()` の順序をテストする。WinUI Window の生成や実体の破棄は UI ランタイム境界のため直接テストしない |
| URL、フォルダー、アイコン、Clipboard | `UrlValidatorTests`、`ClipboardServiceTests`、各UIの手動確認 | `Process.Start`、ファイル読み込み、Clipboardは直接I/Oのため、fake可能なServiceを追加して失敗時を単体テストする。Clipboard の OS 境界は #291 で抽出済み |

## 5. 抽出計画

以下は後続の #286 を、影響範囲と検証単位で分割するための候補である。
順序は固定行数の削減量ではなく、責務境界と回帰リスクで決める。

| 優先 | 抽出単位 | 主な対象 | 受け皿候補 | 必要な検証 |
| ---: | --- | --- | --- | --- |
| 1 | 外部起動・OS境界 | `CopyToClipboard`（URL 起動は #286 初回実装、フォルダー起動は #289、ウィンドウアイコンは #290、Clipboard は #291 で移行済み） | `IClipboardService`、アイコン Service | fake を使う失敗・引数テスト、URL・フォルダー・アイコン・Clipboard を含む #166 の該当 GUI 確認 |
| 2 | コピー・通知フィードバック | `CopyLaunchCommand`、`ShowCopyFeedback`、`OnCopyFeedbackExpired` | `CopyFeedbackCoordinator`（コピー通知の文言・表示期限・キャンセルを抽出済み） | `CopyFeedbackCoordinatorTests`で文言・キャンセル・失敗通知を検証し、コピーGUI確認 |
| 3 | 購読状態・トレイ実行 | `OnTrayRightClickCommandExecuteRequested`、`ExitApplication`（購読状態表示は `SubscriptionStateCoordinator` へ、コマンド振り分けは `TrayCommandCoordinator` へ、終了処理は `WindowLifecycleCoordinator` へ抽出済み） | TrayCommand/Lifecycle Coordinator | コマンド振り分けは `TrayCommandCoordinatorTests`、終了要求は `WindowLifecycleCoordinatorTests` で検証。トレイGUI確認は #295 で完了 |
| 4 | イベント一覧・レビュー通知 | `HandleReviewEvent`、`OnDismissEventClick`、`ShowReviewNotification`、`ReviewNotificationFormatter` | ReviewNotification Coordinator/Presentation | 上限・終了済み・自動起動結果・通知文言のテスト、通知GUI確認 |
| 5 | 設定・レートリミット境界 | `OnSettingChanged`、各設定Toggle、`OnRateLimitAgentOptionChanged`、`OnToggleRateLimitReminderClick` | Settings/RateLimit Coordinator | 初期化中・変更時・Schedule/Cancelのテスト、設定GUI確認 |
| 6 | GatewayログインUI lifecycle | `StartGatewayLoginAsync`、`HandleLoginResultAsync` | Login Dialog ControllerまたはUI向けCoordinator | 成功・失敗・キャンセル・再入のテスト、実機GUI確認 |
| 7 | 構成ルート整理 | コンストラクター内のCoordinator/Service生成 | `App`またはcomposition root | 依存グラフと起動・終了の回帰確認 |

各単位は一つのPRへ詰め込まず、実装時に300行以内を目安としてさらに分割する。
WinUIのDialog、DispatcherQueue、Window生成は、テスト不能なUIランタイム境界として
無理に純粋ロジックへ変換しない。一方、そこへ判断・状態・I/Oを持ち込まない。

## 6. 完了判定

本棚卸しの成果物は本書である。#285の完了時には、次を確認する。

- フィールド、静的データ、Win32宣言、コンストラクター、全メソッドが本書に記載されている
- 各項目に、維持・境界確認・抽出の判定と理由、または推奨する受け皿がある
- 既存テストと不足するテスト、必要なGUI E2Eが経路ごとに記録されている
- #286の実装単位と分割方針が、行数ではなく責務境界と回帰リスクに基づいている
- 行数とカバレッジは抽出前後の結果として記録され、完了条件の数値合わせに使われていない
- 本書を参照して、親Issue #262の残作業と後続Issueの状態を更新できる

## 7. 関連

- 親Issue: #262
- 後続Issue: #286
- 手動E2Eランブック: #166
- code-behind方針: `AGENTS.md`、`docs/rules.md`
- 行数チェック: `scripts/check-code-behind-size.ps1`
