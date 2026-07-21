# 実機確認レポート: PR #197 / PR #198（2026-07-21）

## 概要

PR #197（issue #183: mcp-gateway 初回認証のアプリ内導線）および PR #198（issue #181: トレイポップアップ通知への全面移行）について、Windows 11 実機上で動作確認を行った。

**結論: PR #198 の中核機能であるトレイポップアップ通知は実機で動作せず、レビューイベントを受信するたびにアプリがプロセスごとクラッシュする。** 単体テストは全件成功（行カバレッジ 92.36%）しているが、クラッシュ箇所は `[ExcludeFromCodeCoverage]` 指定の `TrayIconService` にあり、テストでは検出できない。

**PR #197 の device flow 自体は正しく動作するが、ログイン処理が終わってもダイアログが自動で閉じず、結果がユーザーに届かない。**

| PR | 判定 |
|---|---|
| #197 | 条件付き OK。device flow・セキュリティ要件は満たすが、ダイアログの自動クローズが機能しない |
| #198 | **NG（致命的リグレッション）** |

## 検証環境

| 項目 | 値 |
|---|---|
| OS | Windows 11 Home 10.0.26200 |
| ビルド | `dotnet build winui3/SquirrelNotifier.WinUI3.sln -c Debug -p:Platform=x64`（0 warning / 0 error） |
| アプリバージョン | 0.5.2.0 |
| 対象コミット | `d9b1463`（PR #198 マージ後の main） |
| 自動化 | windows-mcp v3.4.4（デスクトップ操作・スクリーンショット） |
| 実施時刻 | 2026-07-21 13:02〜13:45 JST |
| mcp-resource-subscriber | v0.5.0（`--login` 対応の v0.3.0 以上） |
| mcp-gateway | `https://localhost:8080/mcp/thread-owl`（認証済み） |

証跡スクリーンショット: `%LOCALAPPDATA%\Temp\claude\C--Users-jojob-src-squirrel-notifier\evidence\`

---

## 検証結果

### PR #197 — mcp-gateway 初回認証のアプリ内導線

| # | 確認項目 | 結果 | 備考 |
|---|---|---|---|
| 197-1 | Settings 内 Gateway URL 行の「ログイン」ボタン | **OK** | `GatewayUrlBox` /「コンテナから自動設定」の右に表示（`01-settings-expanded.png`） |
| 197-2 | 認証エラー時の `AuthRequiredInfoBar` 自動表示 | **OK** | subscriber が `AUTH_LOGIN_REQUIRED` を返す状態を作り再現。リトライ 5 回超過後に Severity=Error の InfoBar「mcp-gateway の認証が必要です」が表示され、ステータスも `Error: mcp-gateway への認証が必要です。…` に更新（`13-auth-required-infobar.png`） |
| 197-3 | InfoBar 側「mcp-gateway にログイン」からの device flow 起動 | **OK** | Settings 側と同一の ContentDialog が起動（`OnLoginToGatewayClick` 共有）（`14-login-version-error.png`） |
| 197-4 | ログイン実行中のボタン無効化・進行状況表示 | **OK** | 実行中は `GatewayLoginButton` がグレーアウト、終了後に復帰。ステータスは「認証を開始しています...」→「ブラウザで認証ページを開きました。…承認待ち...」と更新される |
| 197-5 | verification URI のダイアログ表示と http/https 限定のブラウザ起動 | **OK** | 承認 URL `https://localhost:8080/activate?user_code=****`（https）と認証コードを表示。ログに `browser open attempted (opened=True)`、Chrome に「Activate Device」タブが実際に開いた（`11-login-dialog.png`） |
| 197-6 | 機密情報をログに出力しない | **OK** | `winui3.log` を `user_code` / 承認 URL / `access_token` / `refresh_token` / `Bearer` で全文検索したが該当なし。記録されるのは `Device authorization received; browser open attempted (opened=True).` のみ |
| 197-7 | subscriber 未設定 / 旧バージョン時のエラーメッセージ | **OK** | `--version` の出力が解釈できない場合、「mcp-resource-subscriber のバージョンを確認できませんでした（--version の出力: "…"）。--login には v0.3.0 以上が必要です。」と原因が特定できる内容で表示（`15-login-failed-dialog.png`）。ただし後述のとおり、この表示に到達するにはユーザーが手動でキャンセルする必要がある |

> 実機の subscriber は v0.5.0 のため、「v0.3.0 未満を検出したときのメッセージ」だけはダウングレードなしに再現できず未検証。バージョン**判定不能**時のメッセージは上記のとおり確認済み。

> device flow のブラウザ側承認は、ユーザー本人の同意行為であるため実行していない。したがって `McpLoginOutcome.Succeeded` 後の処理（成功ダイアログ・`AuthRequiredInfoBar` のクローズ・購読の自動再開）は未検証。

### PR #198 — トレイポップアップ通知への全面移行

| # | 確認項目 | 結果 | 備考 |
|---|---|---|---|
| 198-1 | トレイアイコン常駐・ツールチップ | **OK** | ツールチップ "Squirrel Notifier"（`06-tray-contextmenu.png`） |
| 198-2 | トレイ左クリックでウィンドウ復帰 | **OK** | 非表示状態から復帰し前面化することを Win32 API（`IsWindowVisible` / `GetForegroundWindow`）で確認 |
| 198-3 | トレイ右クリックで XAML MenuFlyout | **OK** | 開く / 購読を開始 / 購読を停止 / アプリの更新を確認 / 区切り / 終了 の 5 項目 + セパレータ。「開く」の動作も確認 |
| 198-4 | レビューイベント受信時のポップアップ表示 | **NG（致命的）** | ポップアップは表示されず、アプリがクラッシュして終了する。詳細は後述 |
| 198-5 | ポップアップのタイトル・本文 | 検証不能 | 198-4 によりブロック |
| 198-6 | reason によるレビューするボタンの出し分け | 検証不能 | 同上 |
| 198-7 | PR URL 不整合時の「PRを開く」非表示 | 到達不能 | 後述（設計上の観察） |
| 198-8 | ポップアップ各ボタンでの閉じ動作 | 検証不能 | 198-4 によりブロック |
| 198-9 | light dismiss | 検証不能 | 同上 |
| 198-10 | レートリミット/接続エラーはバルーン通知のまま | **OK** | 接続エラー時にバルーン通知が表示されることを確認。`ShowNotification` 経路は正常 |
| 198-11 | 通知先未登録時の例外によるリトライ可能化 | 未検証 | UI スレッドへの enqueue 失敗を実機で再現できず |

---

## 致命的欠陥: レビュー通知受信でアプリがクラッシュする

### 症状

購読中にレビューイベントを 1 件受信すると、通知ポップアップは一切表示されず、`SquirrelNotifier.WinUI3.exe` がプロセスごと異常終了する。アプリ自身のログには例外が残らず、Windows イベントログにのみ記録される。

アプリログ（最後の行がイベント受信、その直後にプロセス消失）:

```
[2026-07-21 13:21:50] Notification payload received: [{"owner":"scottlz0310","repo":"squirrel-notifier","prNumber":198,...,"reason":"opened",...}]
（以降ログなし。プロセス終了）
```

Windows イベントログ（Application / Application Error, ID 1000）:

```
障害が発生しているアプリケーション名: SquirrelNotifier.WinUI3.exe、バージョン: 0.5.2.0
障害が発生したモジュール名: CoreMessagingXP.dll、バージョン: 10.0.27200.1024
例外コード: 0xc000027b        ← STATUS_STOWED_EXCEPTION
フォールト オフセット: 0x0000000000093b66
```

Windows Error Reporting（ID 1001）の内部例外コード:

```
P4: combase.dll
P7: 8000ffff                 ← E_UNEXPECTED
```

### 再現性

| 条件 | 結果 |
|---|---|
| メインウィンドウ非表示（トレイ最小化）+ reason=opened | クラッシュ |
| メインウィンドウ表示中 + reason=opened | クラッシュ |
| メインウィンドウ表示中 + reason=closed（レビューするボタン非表示ケース） | クラッシュ |

**3/3 で再現。** ウィンドウの表示状態やイベント内容には依存しない。

### 原因箇所の特定

`MainWindow.xaml.cs:1119` の `_trayIconService.ShowReviewPopup();` を一時的にコメントアウトして再ビルドし、同一のイベントを流したところ、**クラッシュせずプロセスが生存し、イベントも正常に処理された**。

したがって原因は以下の経路に確定:

```
MainWindow.OnReviewEventReceived
  └─ TrayIconService.ShowReviewPopup()          （Services/TrayIconService.cs:42）
       └─ TaskbarIcon.ShowTrayPopup(Point.Empty)
```

※ 検証用の一時変更は復元済み（`git status` クリーン、リビルド済み）。

### 原因の仮説

`MainWindow.xaml` では `TaskbarIcon.TrayPopup` の中身を `<Popup>` で包んでいる:

```xml
<tb:TaskbarIcon.TrayPopup>
    <Popup IsLightDismissEnabled="True">
        <local:ReviewNotificationPopup x:Name="ReviewNotificationContent"/>
    </Popup>
</tb:TaskbarIcon.TrayPopup>
```

H.NotifyIcon は `TrayPopup` に渡された `UIElement` を、自前のポップアップホスト（別ウィンドウ）で表示する実装になっている。そこへ、XamlRoot に紐づく必要がある `Popup` 要素をさらに入れ子にしているため、表示時に WinRT 側で `E_UNEXPECTED` が発生していると考えられる。障害モジュールが `CoreMessagingXP.dll` / `combase.dll` であることとも整合する。

### 推奨対応

1. `<Popup>` のラップを外し、`ReviewNotificationPopup` を `TrayPopup` に直接置く（light dismiss は H.NotifyIcon 側の設定で行う）。
2. それで解決しない場合は `ShowTrayPopup` を使わず、自前のトップレベルウィンドウでポップアップを描画する方式に切り替える。
3. いずれの場合も、**通知表示処理を `try/catch` で保護し、失敗時はログに記録した上でバルーン通知へフォールバックする**（現状はハンドルされない XAML 例外がプロセスを落とすため、ユーザーは原因を知る手段がない）。
4. 回帰防止のため、`ShowReviewPopup` を含む通知表示経路の実機スモークテストを手順化する。

### なぜ CI で検出できなかったか

- `TrayIconService` は `[ExcludeFromCodeCoverage]` 指定であり、単体テストが存在しない。
- `NotificationServiceTests` / `ReviewNotificationPolicyTests` はイベントの分岐とポリシーのみを検証しており、実際の XAML 表示呼び出しには到達しない。
- 単体テストは全件成功する（行 92.36% / 分岐 85.87% / メソッド 95.22%）。

---

## 欠陥: ログイン完了後もダイアログが自動で閉じない（PR #197）

### 症状

ログイン処理が終了しても ContentDialog が「認証を開始しています...」の表示のまま残り続ける。ユーザーが「キャンセル」を押して初めてダイアログが閉じ、その直後に結果ダイアログ（「ログインに失敗しました」等）が表示される。

つまり、**ユーザーから見ると「認証を開始しています...」でフリーズしたように見え、失敗理由に自力ではたどり着けない。**

### 再現性

subscriber のバージョン確認が約 3 秒で失敗する状態にして「mcp-gateway にログイン」を押した場合:

| 経過 | ダイアログ表示 |
|---|---|
| 3 秒 | 認証を開始しています... |
| 10 秒 | 認証を開始しています... |
| 20 秒 | 認証を開始しています... |
| 35 秒 | 認証を開始しています... |
| キャンセル押下直後 | 「ログインに失敗しました」＋原因メッセージ |

2 回試行して 2 回とも同じ挙動。

### 該当コード

`MainWindow.xaml.cs` の `StartGatewayLoginAsync`:

```csharp
IAsyncOperation<ContentDialogResult> showOperation = dialog.ShowAsync(ContentDialogPlacement.Popup);
Task<Models.McpLoginResult> loginTask = loginService.LoginAsync(cts.Token);

// 認証完了時にダイアログを自動で閉じ、ShowAsync を終了させる
_ = loginTask.ContinueWith(
    _ => DispatcherQueue.TryEnqueue(dialog.Hide),
    TaskScheduler.Default);
```

この `dialog.Hide()` が実機で効いていない。コード上のコメントは `ShowAsync` を先に開始することで「`dialog.Hide()` が `ShowAsync` より先に走る」競合を避ける意図を述べているが、ログイン処理に 3 秒かかっている（＝ダイアログは十分開き切っている）ケースでも閉じないため、単純な開始順の競合では説明できない。`ContentDialogPlacement.Popup` で表示したダイアログに対する `Hide()` の扱い、または `DispatcherQueue.TryEnqueue` の戻り値を確認する必要がある。

### 影響範囲

失敗パスで確認したが、成功パス・タイムアウトパスも同じ `dialog.Hide()` に依存しているため、同様に自動クローズしない可能性が高い（成功パスは未検証）。

### 推奨対応

1. `ContinueWith` 内で `DispatcherQueue.TryEnqueue` の戻り値を確認し、失敗時はログに残す。
2. `dialog.Hide()` ではなく、`ContentDialog` に `PrimaryButton` を持たせて `showOperation.Cancel()` する、あるいはダイアログを開いたまま結果テキストを差し替えて閉じるボタンを出す方式へ変更する。
3. 最低限、ログイン処理の完了・失敗が UI に反映されない状態を放置しないよう、タイムアウト時のフォールバック表示を追加する。

---

## 付随して発見した問題

### 1. preflight 失敗時にエラーメッセージが空になる

`McpSubscriptionService.PreflightCheckAsync` は、subscriber プロセスの**起動に失敗した場合**は `LastError` を設定するが、**起動できて終了コードが非 0 だった場合**は `LastError` を設定せずに `false` を返す（`Services/McpSubscriptionService.cs:241-283`）。

結果、`RunSubscriptionLoopAsync` が `State = Error` にした後、UI 側は空文字を表示する:

```
[2026-07-21 13:13:13] [UI] Updating tray icon to error state. Error:
[2026-07-21 13:13:13] [UI] Showing connection error balloon notification.
```

バルーン通知も「接続エラー: 」とだけ表示され、ユーザーは原因を判断できない。終了コードと stderr を含めたメッセージを `LastError` に設定すべき。

※ これは #197 / #198 で入った変更ではなく既存の挙動。

### 2. トレイの MenuFlyout がライトテーマで描画される

Windows はダークモード（`HKCU:\...\Themes\Personalize` の `AppsUseLightTheme = 0`、`SystemUsesLightTheme = 0`）であり、アプリ本体ウィンドウもダークで描画されるが、トレイ右クリックの MenuFlyout だけが白背景・黒文字のライトテーマで表示される（`06-tray-contextmenu.png`）。

#198 で Win32 のトレイメニュー（`Helpers/TrayContextMenu.cs`、削除済み）から XAML MenuFlyout に移行した結果と考えられる。MenuFlyout をホストするポップアップウィンドウにアプリの `RequestedTheme` が伝播していない。

### 3. 購読中でも「購読を開始」が活性のまま

`MainWindow.xaml` の `TrayStartCommand` / `TrayStopCommand` には `CanExecuteRequested` が設定されておらず、購読中でも「購読を開始」、停止中でも「購読を停止」が押せる。メインウィンドウの Start / Stop ボタンは正しく活性制御されているため、トレイメニューとの挙動が非対称。

### 4. `CheckSubscriberVersionAsync` が stderr を読まない

`McpLoginService.CheckSubscriberVersionAsync`（`Services/McpLoginService.cs:275`）は `RedirectStandardError = true` としながら stderr を一切読み出さずに `WaitForExitAsync` を待つ。subscriber が stderr にパイプバッファ（既定 4KB 程度）を超える出力を行うと、子プロセスが書き込みでブロックし `WaitForExitAsync` が返らなくなる。実機では該当出力量に達しなかったため顕在化していないが、`LoginAsync` 本体（stderr を `ReadToEndAsync` している）との実装差でもあるため揃えるべき。

### 5. ログインダイアログのラベルが常時表示される

`承認 URL:` と `認証コード:` のラベルは、値が届く前（`Visibility.Collapsed` なのは値の TextBox 側のみ）から表示されている。認証開始直後はラベルだけが並んで見え、何を待っているのか分かりにくい。

### 6. `UrlValidator` による「PRを開く」非表示は到達不能

`ReviewNotificationPopup.SetReviewEvent` は `UrlValidator.IsSafeGitHubUrl` の結果で `OpenPrButton` の表示を切り替えるが、`ReviewEventParser` は以下のいずれかしか UI に渡さない:

- `ConvertToEvent` 経由: `prUrl` を `owner` / `repo` / `prNumber` から組み立てるため常に整合する
- 完成済み `ReviewEvent` 経由: `IsSafeGitHubUrl` を通らないイベントは `events` に追加されない

したがって、UI 層のこの分岐に不整合な URL が到達することはない。多層防御としては妥当だが、198-7 は実機では検証不能である。

---

## 検証手法（再現手順）

実 gateway / GitHub に一切書き込まずにレビューイベントを注入するため、subscriber の出力契約に沿ったスタブを使用した。

`McpSubscriptionService` が subscriber に要求する契約:

- `--help` で終了コード 0 を返すこと（preflight）
- `--url <gateway> --uri <resourceUri> --timeout-ms <n> --json` で起動され、stdout に `SubscriptionResult` の JSON を返すこと
- イベントなしの満了時は `{"route":"timeout","errorCode":"NOTIFICATION_TIMEOUT"}`（終了コードは非 0 でよい）
- イベントありの場合は `{"route":"subscription","notificationReceived":true,"finalText":"<ReviewCandidate の JSON 配列>"}`
- 認証必須状態の再現には `{"route":"failed","errorCode":"AUTH_LOGIN_REQUIRED"}`（197-2 の検証に使用）

スタブ配置先: `%LOCALAPPDATA%\Temp\claude\C--Users-jojob-src-squirrel-notifier\stub\`

手順:

1. 設定の Command Path をスタブに変更し、Stop → Start で再購読
2. `trigger.json` に `ReviewCandidate` 配列を書き込む
3. 次の subscriber 起動サイクル（約 5 秒）でスタブが payload を返し、アプリが通知処理に入る

## 環境側の指摘（アプリの不具合ではない）

検証開始時、`settings.json` の `SubscriberCommandPath` が実在しないパス `C:\Users\jojob\AppData\Local\pnpm\bin\mcp-resource-subscriber.cmd` を指しており、起動直後に preflight が失敗していた。実体は `C:\Users\jojob\.bun\bin\mcp-resource-subscriber.exe` にあるため、検証終了時点でこちらに設定し直してある（元の値は起動不能なため復元していない）。

## 残タスク

優先度順:

- [x] **PR #198 のクラッシュ修正**（#199 で実施）
- [x] **PR #197 のログインダイアログ自動クローズ修正**（#200 / 失敗理由がユーザーに届かない）
- [x] 修正後に 198-4 〜 198-9 を再検証（#199 修正時に実施）
- [x] device flow の成功パス（成功ダイアログ・購読再開）を確認（#200 修正時に、device flow 出力を模擬するスタブで実施）
- [ ] 実 gateway の device flow 成功パス（トークン取得・`tokens.db` への書き込み・成功ダイアログ・InfoBar クローズ・購読再開）を、ユーザー本人の承認を伴って確認
- [x] preflight 失敗時の `LastError` 未設定を修正（#201 で実施）
- [x] `CheckSubscriberVersionAsync` の stderr 未読を修正（#201 で実施。`EnqueueReviewService` の同一パターンも併せて修正）
- [ ] トレイ MenuFlyout のテーマ追従
- [ ] トレイメニューの Start / Stop の活性制御
- [x] ログインダイアログのラベル表示制御（#200 で実施）
