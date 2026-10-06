# NuGetキャッシュの比較実験（#184）

恒久的なCI設定を変える前に、現行キャッシュと空のパッケージ領域でCodeQLの
セットアップ・restore・ビルド・解析時間を比較する。通常のCIと品質ゲートは維持する。

## 条件

- 対象ソースはmainの `fa5f43a1337fa56fdccd6239fe64a2e62acdd808` に固定する。
- SDKは対象ソースの `global.json` と現行 `10.0.x` setupを使用し、実際の版を結果へ残す。
- CodeQLは現行CIログで確認した2.27.1、manual build、全solutionと既定クエリを使う。
- `windows-2025` のrunnerで各条件3回、同時実行上限2とする。workflow間もconcurrencyで直列化し、重複起動を同時実行しない。
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
C#抽出ファイルの分類・行数・内容hashを記録する。行数はLF分割の件数（末尾改行の空要素も含む）として条件間で統一する。job失敗は成功標本へ置換しない。
ログとjob APIからセットアップ・restore・build・analysisの内訳も照合する。

比較時は条件別中央値だけでなく各標本のばらつきを確認する。手書きソースの抽出漏れ、
生成コードの減少、解析エラーがある場合は高速化として採用しない。
パッケージ配置先や生成コード中の絶対パスによりhashが変わる場合は、差分の理由を確認する。
ルール数・結果数の一致だけでは解析完全性の証明にしない。

この実験は現行CIの最適化候補の比較であり、過去baselineに対する30%短縮達成とは区別する。
恒久変更、予算変更、#184のクローズは実測結果を確認してから判断する。

CodeQLのmanual buildと出力制御は、[公式init定義](https://github.com/github/codeql-action/blob/v4/init/action.yml)
および[公式analyze定義](https://github.com/github/codeql-action/blob/v4/analyze/action.yml)に従う。

## 3標本の結果（2026-10-07）

[run 37525056109](https://github.com/scottlz0310/squirrel-notifier/actions/runs/37525056109)
（workflow HEAD `baf2b70115d77b383585a1107b2fbd7d177c8984`）で6jobがすべて成功した。
測定対象ソースは上記のmain SHAで、workflowの変更は測定対象へ混ぜていない。

| 標本 | cached | empty | 差（cached−empty） |
|---|---:|---:|---:|
| 1 | 462.762秒 | 387.434秒 | 75.328秒 |
| 2 | 459.641秒 | 383.997秒 | 75.644秒 |
| 3 | 421.056秒 | 292.380秒 | 128.676秒 |
| **条件別中央値** | **459.641秒** | **383.997秒** | **75.644秒** |

条件別中央値は約16.46%短縮した。ただしemptyは292〜387秒とばらつきがあり、
通常のPR CI全体の壁時計や旧baselineに対する30%短縮達成とは扱わない。
計測区間はsetup直前から解析直後までで、CodeQLのpost処理やartifact uploadは含まない。

| 標本・条件 | Setup | CodeQL init | restore | build | analysis |
|---|---:|---:|---:|---:|---:|
| 1 cached | 172秒 | 65秒 | 9秒 | 123秒 | 94秒 |
| 1 empty | 3秒 | 88秒 | 28秒 | 156秒 | 112秒 |
| 2 cached | 76秒 | 67秒 | 13秒 | 186秒 | 117秒 |
| 2 empty | 3秒 | 69秒 | 21秒 | 180秒 | 109秒 |
| 3 cached | 71秒 | 67秒 | 9秒 | 169秒 | 104秒 |
| 3 empty | 2秒 | 64秒 | 24秒 | 110秒 | 92秒 |

SDK10.0.401、runner image20260925.250.1、CodeQL2.27.1は全6標本で一致した。
ログ上の問題検出クエリ44件＋metric8件のID集合も全6標本で一致した。
空の領域のrestoreは21〜28秒であり、キャッシュ復元を省いた利点を打ち消していない。
cachedの復元後パッケージ量は約4.93GB / 10,903ファイル、emptyは約2.87GB / 6,474ファイル。
単純なcache有無だけでなく、復元する既存領域の肥大化も要因候補となる。

初期collectorには、ZIP内のドライブ表現（D_）の正規化と、SARIFのdriver/extensionsの
集計に不備があった。測定時間・rawファイル一覧・行数・hashは取得できているため、
分類を再計算し、クエリIDは実行ログから照合した。artifact中の初期kind・rules=0は
確定根拠として使わない。修正版はSARIFとsrc.zipも保存し、driverとextensionsの両方を読む。

再分類した結果は、全6標本で手書き303ファイル/51,232行（hashも完全一致）、
生成32ファイル/9,607行、依存3ファイル/85行だった。
生成コードは同じcached同士・empty同士でもhashが変わるため、ファイル数・行数の一致だけで
同等性を断定しない。追加1組のsrc.zipで内容差を確認してから採用判断する。

## 追加証跡の確認

[run 37528112101](https://github.com/scottlz0310/squirrel-notifier/actions/runs/37528112101)
（workflow HEAD `37c5dd9631f618184b506abc2446beae0c8475c8`）で追加1組も成功した。
SDK・image版は上記と一致し、cached460.879秒、empty441.585秒（差19.294秒）だった。
追加標本は証跡確認用として別記し、主測定の3標本の中央値に混ぜていない。
この差の縮小も踏まえ、短縮幅を常に75秒と保証しない。

SARIFではCodeQL2.27.1、csharp-queriesのルール52件・ID集合一致、結果0件を確認した。
driverはルールを持たず、クエリ定義はextensionsに入っていた。
ルールを持たないextensionのnullは0件として数えるよう、最終collectorも修正した。
この補正は保存された実SARIFをローカルで再解析して確認した。

生成コードの内容差は、共通Vtableの代表クラス名の選択、型名条件の順序、宣言順序に見られた。
WinRTCCWVtableの7クラスは名前を除いた本文のhash集合が完全一致し、参照側の名前も対応した。
WinRTGenericInstantiationは行内容が揃い、宣言順序だけが変わっていた。
生成コードを除外したり、buildlessへ切り替えたりした差ではない。
全dataflowの同等性まで証明するものではないため、raw src.zipとSARIFも証跡として残す。

## 次の判断

候補はsecurity-scanだけを専用の空のNuGet領域へ変更し、同じmanual build・クエリ・品質ゲートで
通常CIを再計測すること。今回の比較はその変更の根拠として使い、恒久変更は利用者の合意後に行う。
他jobへの適用やキャッシュの削除はこの実験の完了に含めない。

#184は、通常PR CIの中央値30%短縮とE2Eの時間予算を確認するまでOPENを維持する。
このPRは比較実験・再計測記録の追加であり、Epic全体を閉じる変更ではない。
