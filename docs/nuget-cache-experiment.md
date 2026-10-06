# NuGetキャッシュの比較実験（#184）

恒久的なCI設定を変える前に、現行キャッシュと空のパッケージ領域でCodeQLの
セットアップ・restore・ビルド・解析時間を比較する。通常のCIと品質ゲートは維持する。

## 条件

- 対象ソースはmainの `fa5f43a1337fa56fdccd6239fe64a2e62acdd808` に固定する。
- SDKは対象ソースの `global.json` と現行 `10.0.x` setupを使用し、実際の版を結果へ残す。
- CodeQLは現行CIログで確認した2.27.1、manual build、全solutionと既定クエリを使う。
- `windows-2025` のrunnerで各条件3回、同時実行上限2とする。
- cachedは対象ソースの共通セットアップと同じNuGetキー・復元規則を使用する。
- emptyはrunnerの新しい専用ディレクトリを `NUGET_PACKAGES` に指定し、キャッシュを復元しない。
- どちらの条件もキャッシュを保存しない。既存キャッシュを削除しない。
- 計測区間はSDKセットアップ直前からCodeQL解析直後まで。証跡集計・upload時間を含めない。
- Code Scanningへの結果・DBアップロードは行わず、測定JSON・SARIF・抽出ソースのsrc.zipをartifactに保存する。

空の領域はrunnerごとに作成するため、ローカルのNuGetパッケージや本番設定には影響しない。
SDK・CodeQL・runner imageの実際の版が条件間で一致しない場合は同条件の比較に使用しない。

## 実行と判定

`.github/workflows/nuget-cache-experiment.yml` は
`workflow_dispatch` で実行する。samplesは通常の3標本と、証跡確認用の追加1標本を選べる。初回の実験は作業ブランチへのpushで起動したが、以後は明示起動だけとする。通常のPR/push CIへ新しい必須jobを追加しない。
対象ソースSHAはworkflowの `SOURCE_SHA` に記録し、別ソースを測る際は明示的に変更する。

各jobの `nuget-experiment-<sample>-<cached|empty>` artifactに `measurement.json` が残る。
ソースSHA、SDK、CodeQL、runner image、測定秒数、パッケージ総量、ルール数、結果数、
C#抽出ファイルの分類・物理行数・内容hashを記録する。job失敗は成功標本へ置換しない。
ログとjob APIからセットアップ・restore・build・analysisの内訳も照合する。

比較時は条件別中央値だけでなく各標本のばらつきを確認する。手書きソースの抽出漏れ、
生成コードの減少、解析エラーがある場合は高速化として採用しない。
パッケージ配置先や生成コード中の絶対パスによりhashが変わる場合は、差分の理由を確認する。
ルール数・結果数の一致だけでは解析完全性の証明にしない。

この実験は現行CIの最適化候補の比較であり、過去baselineに対する30%短縮達成とは区別する。
恒久変更、予算変更、#184のクローズは実測結果を確認してから判断する。

CodeQLのmanual buildと出力制御は、[公式init定義](https://github.com/github/codeql-action/blob/v4/init/action.yml)
および[公式analyze定義](https://github.com/github/codeql-action/blob/v4/analyze/action.yml)に従う。
