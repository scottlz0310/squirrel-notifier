# リリースの準備と公開

`scottlz0310/release-automate` v2.0.0 の prepare / publish / finalize をすべて
`fd3e676a431bbd6e189270e6911ec4c2307e9f2b` に固定する。Squirrel Notifier 側は x64 の配布物生成、
日本語ノートの合成、CI・ランブック・配布物の検証を担当する。安定版 `X.Y.Z` のみを受け付ける。

## 準備 PR

1. main の `Prepare Release` を手動実行し、`target_version` に `0.17.0` のような安定版を指定する。
2. GitHub App が `release/vX.Y.Z` の PR を作成する。csproj、CHANGELOG のバージョン節、比較リンクを確認する。
   本文は日本語の `.github/RELEASE_PREPARATION_TEMPLATE.md` に置き換える。
3. 準備の変更をすべて済ませてから、実装セッションとは別のセッションで
   [実デスクトップのランブック](../tests/e2e/agent-runbooks/README.md) を実行する。追加コミットは結果 JSON だけにする。
4. 同一 HEAD の CI・Runbook Guard の成功と独立レビュー、未解決スレッド 0 件を確認する。
5. 明示的なマージ許可を得て Squash merge する。コミットのタイトルを **`chore(release): vX.Y.Z`** にする。
   通常の PR 番号の接尾辞を付けず、この形式を維持する。

Organization の `RELEASE_BOT_APP_ID` / `RELEASE_BOT_PRIVATE_KEY` を準備 workflow に渡す。
鍵の管理・ローテーションは release-automate 側の責務で、個別 repo に秘密鍵を置かない。

## 公開承認

`Release` は main へのリリースコミットだけで開始し、最初の `authorize` ジョブが
GitHub Environment **`release`** の承認を待つ。マージだけではタグ・draft の作成を開始しない。

Environment は次の保護設定を必須とする。workflow の名前だけでは承認を強制できないため、
GitHub 側の設定も維持する。

- required reviewer: `scottlz0310-user`
- deployment branch: `main` のみ
- 管理者による承認のバイパス: 無効
- 自己承認の禁止: 無効（個人開発でもマージと公開を別の操作として承認できるようにする）

公開する対象 SHA と version を確認してから Environment を承認する。
エージェントはマージ許可を公開許可として扱わず、承認操作を自律実行しない。

## 承認後の流れ

1. タイトル・csproj・CHANGELOG の安定版の一致を検証する。
2. reusable publish が main のトリガー SHA に固定したタグと **draft** を作成する。
3. 同じ SHA を checkout して既存の `Build-ReleasePackage.ps1` で ZIP / MSI / checksum を生成する。
   同時に同じ SHA の `build-and-test` / `headless-e2e` / `distribution-e2e` / `lint` の成功を待ち、
   直前の main を base としてランブックの結果を再検証する。
4. `extract-release-notes.ps1` が CHANGELOG と RELEASE_TEMPLATE を合成する。
   固定 SHA の draft であることを確認し、配布物と日本語ノートを添付する。
5. draft から配布物をダウンロードし、ノート一致、ZIP / MSI の SHA256、ZIP の必須ファイル、
   MSI の ProductVersion と同一版の MajorUpgrade、EXE の `X.Y.Z+<対象 SHA>` を検証する。
6. 検証成功を `needs` で受けた reusable finalize だけが公開する。

タグは GITHUB_TOKEN で作成するため、タグ push から別 workflow が起動することに依存しない。
一連の caller の依存関係で最後まで接続する。配布物の名前と日本語ノートの原稿は変更しない。

## 失敗・再実行

検証失敗時は draft のまま停止する。原因を修正した上で同じ run を再実行する。
同じ SHA の draft は再利用し、添付ジョブが asset を置換した後、毎回再取得・検証する。
タグが異なる SHA を指す場合は停止する。コードの修正で対象 SHA が変わる場合は、
新しい準備とランブックの再実行が必要になる。タグの移動・削除を自動で行わない。
公開済み Release は再実行しても添付・公開し直さない。

公開済みの本文の修正には既存の `Reapply Release Notes` を使用する。
既定の preview を確認し、明示許可後に `apply: true` を使う。

初回の実リリースでは、公開後のタグ SHA、Release の targetCommitish、3 つの配布物、
日本語ノート、latest を確認し、#443 の運用検証の証跡とする。
