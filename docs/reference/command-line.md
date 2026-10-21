# 管理 CLI のコマンド

[ドキュメント一覧](../README.md)

コマンドラインから接続先を選択し、設定・履歴・更新を管理するためのリファレンスです。例はリポジトリのルートから実行してください。配布物では CLI の実行ファイルに読み替えられます。

管理 CLI は、ローカルの ClientHost を通して、選択したクラスター・ピアの状態確認や管理操作を行います。`--url` には ClientHost のループバック HTTP URL を指定してください。`--help` は接続なしで利用できます。CoreService を前景起動する `service run` も URL の指定は不要です。

```powershell
dotnet run --project src/ReArtWord.Contents.BackgroundService.CommandLine -- --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a status
dotnet run --project src/ReArtWord.Contents.BackgroundService.CommandLine -- --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a catalog
```

## コマンド一覧

以下では実行ファイルを `contents-cli` と表記します。配布物の `files/cli/ReArtWord.Contents.BackgroundService.CommandLine.exe` に読み替えてください。URL はローカル ClientHost の待受に合わせて指定してください。管理操作は `--cluster` と `--peer` の両方が必須です。別のピアへの自動切替はありません。本文は管理 API と同じ camelCase JSON オブジェクト、enum は camelCase 文字列です。

`--body -` は標準入力、`--body path.json` は UTF-8 ファイルを読みます。

| コマンド | 引数/本文 |
|---|---|
| `network get` | 自身のNIC/IPと全監視の待受状態。`bindings` に割当ごとの状態。クラスター・ピアの指定は不要 |
| `network set` | `--body binding.json`。interfaceId, bindAddress, broadcastAddress, portを明示。既存の全監視をこの一件に置換。クラスター・ピアの指定は不要 |
| `cluster list` | 受信したクラスター・ピアの候補。クラスター・ピアの指定は不要 |
| `request status` | `--id request-guid`。ClientHost が保持する元の要求の結果。クラスター・ピアの指定は不要 |
| `status`, `catalog` | 本文なし |
| `workflow list` | 本文なし。軽量な最初のページ（entries,cursor） |
| `workflow query` | `--body` にlimit/cursor/maximumBytes。次のページを明示取得 |
| `workflow definition-chunk` | `--body` に `id`・`revision`・`effective`・`offset`・`length` を指定。最大65536 バイト断片 |
| `execution list`, `execution query` | 最初のページ、または照会本文で次のページ。Report省略時Summary/DetailReasonを表示 |
| `workflow get` | `--id workflow-id` |
| `workflow validate` | `--body definition.json` (WorkflowDefinitionそのもの) |
| `workflow save` | `--body save.json` (definition, expectedRevision, activationPolicy) |
| `workflow activate` | `--body activate.json`：`workflowId`・`revision`・`impactConfirmed` |
| `workflow run` | `--body run.json`：`workflowId` は必須、`revision`・`entryNodeId`・`inputs` は任意 |
| `workflow run-status`, `run-status` | `--id operation-guid` |
| `workflow operation` | 保存結果の照会 `--id operation-guid` |
| `source stop` | `--body stop.json`。`status`で確認したworkflowId、nodeId、待受ノードのidをsourceId、ピアのinstanceIdをpeerInstanceIdに指定。cancel=falseは受付だけを正常終了 |
| `cluster detach`, `cluster rejoin` | 本文なし |
| `secret set` | `--name secret-name --stdin` または `--name secret-name --env ENV_NAME` |
| `snapshot list` | 状態本文なしのメタデータ最初のページ（entries,cursor） |
| `snapshot query` | `--body` にlimit/cursor/maximumBytesと任意peerId/targetId/kind。明示した次のページまたは範囲だけ取得 |
| `snapshot capture` | `--body capture.json` (target) |
| `snapshot restore` | `--body restore.json` (selections, atは任意) |
| `snapshot capture-status` | `--id` で取得操作を照会 |
| `snapshot restore-status` | `--id` で復元操作を照会 |
| `snapshot recover` | `--body` に元の SnapshotRestoreRequest。本文の operationId 必須 |
| `file operation` | `--id` でファイル全対象操作のphase結果を照会 |
| `file recover` | `--id` の既存ファイル操作を回復。本文・新IDは指定しない |
| `safety list` | 現ピアのloadedと取込状態を照会 |
| `safety status` | `--id` で今回の受付結果を読取専用照会 |
| `safety set` | `--body safety.json` (workflowId,scope,stopped,expectedVersion) |
| `update start` | `--body update-request.json` (マニフェスト,payloadRoot,peerIds,failurePolicy等。variantsもそのまま送信) |
| `update status` | `--id` で更新の全体・ピア別結果を照会 |
| `log query` | `--body log-query.json` (空オブジェクトも可) |
| `operation status` | クラスター永続操作の照会 `--id operation-guid`。スナップショット全体結果の代替ではありません。 |

保存・有効化・実行・スナップショット取得/復元・安全制御変更・更新開始は、本文の `operationId` を維持します。本文にない場合は `--operation-id GUID`、それもなければ新規 GUID を生成します。両方を指定した値が異なる場合は送信しません。送信前にstderrへIDを表示し、通常出力のJSONにも含めます。復元などを照会・回復するため、元の本文ファイルとIDを保持してください。CLIは元ファイルを変更しません。

```powershell
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a workflow run --body samples/cli/run.json
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a run-status --id 11111111-1111-4111-8111-111111111111
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a log query --body samples/cli/log-query.json
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a secret set --name provider-key --env CONTENTS_PROVIDER_KEY
```

秘密値をコマンド引数へ指定する形式はありません。環境変数名だけを指定するか、別プロセスから標準入力をリダイレクトします。標準入力の内容は改行を含めそのまま使用し、対話入力による端末echoを避けるためリダイレクト必須です。秘密設定のAPI応答本文は成功/失敗にかかわらず表示しません。環境変数の設定方法やパイプ元に秘密のリテラルを書くとshell履歴に残るため、既存の秘密管理機構から渡してください。

通常出力は `{operationId,requestId,httpStatus,exitCode,response}` のJSONです。終了コードは 0=確認済、2=入力不正、3=HTTP error、4=業務失敗/未実行/一部成功/検索不完全、5=不明/処理中、6=送信前または読取時のAPI接続不能、130=読取取消またはタイムアウト。副作用要求の通信切断/timeoutは実施有無を断定できないため5です。HTTP202や一部phaseだけ成功を0にはしません。

保存が成功して有効化が未実行の場合も4で各phaseを表示します。`status`の0は照会できたことを示し、全ピア Readyを保証しません。

HTTP タイムアウトは30秒、リダイレクトとproxyは使用しません。要求の自動再試行や自動再送はありません。JSON入力とHTTP応答の上限はそれぞれ16Mi文字/16MiB、秘密入力は64Ki文字です。受理後の接続切断は元操作の取消を意味しません。

回復操作は新しい要求への作り直しではありません。`snapshot recover` は元の本文と同じ操作 ID が必要です。`file recover` は既存操作 ID をパスへ指定し、別の `--operation-id` や本文を受け付けません。照会結果がnull、別の操作ID、更新が未完了、安全制御が未読込の場合は成功終了しません。

```powershell
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a snapshot restore-status --id 33333333-3333-4333-8333-333333333333
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a snapshot recover --body samples/cli/restore.json
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a safety set --body samples/cli/safety.json
```

安全制御の `expectedVersion` は表示時の参考版です。新しい操作IDは現コーディネーターの受付順で適用し、古い表示版だけで後着操作を拒否しません。同じIDは元の受付結果に固定されます。一時続行は `continued:true`、一時停止は `stopped:true`、両falseは通常判断へ復帰します。両trueは指定できません。停止変更のACKは新規入力に対する停止状態の保存であり、開始済み実行の停止確認ではありません。

更新本文は管理APIの ClusterUpdateRequest で、multi-RIDの `variants` はマニフェストとともに保持します。

POST/PUT は `X-Contents-Request` を送信前に一度固定します。業務 operationId があれば同じID、なければ新規IDを使用し、stderr と通常結果の requestId に表示します。不明時は本文を作り直して再送せず、まず `request status --id` で元要求を照会してください。bridgeの受付成功だけでは業務成功と表示しません。

最初に `contents-cli --url http://127.0.0.1:5081 network get` で自身のNICを取得し、次の形式のJSONを `network set --body binding.json` へ渡します。その後 `cluster list` の実際に取得できた一覧から管理対象を選択してください。例のUDPポート47740はCoreServiceの `[cluster].port` と合わせます。

`network set` は全監視を一件へ置き換えるため、複数NICの追加・削除は画面の **Select connection** で行ってください。最大32件で、同じNICとUDPポートは重複登録できません。

```json
{"interfaceId":"実際のNIC ID","bindAddress":"192.168.1.10","broadcastAddress":"192.168.1.255","port":47740}
```

一覧は全件ではなくバイト上限のある一pageです。次のcursorがある場合、同じ照会条件とcursorで明示取得してください。ログの `omissions`、実行の `summary`・`detailReason`・`detailsUnavailable` は成否と別の詳細省略情報です。`workflow get` が本文省略を返した場合は終了code4で知らせます。

完全定義は savedRevision/effectiveRevision を固定した `workflow definition-chunk` で順に取得し、offset・totalBytes・全体SHA256を検証するまで断片を定義として使用しないでください。CLIは全pageや断片を自動再試行しません。

`--display-name "表示名"` は任意の操作記録用メタデータ（最大120文字）です。認証や権限を与えるものではありません。未指定・空欄なら表示名ヘッダーを送りません。秘密値自体は表示名に含めないでください。

スナップショット一覧には値本文を含めません。`scopeOmitted:true` または `detailReason` のある候補は範囲を完全には確認できないため、全体対象として復元に読み替えないでください。同じ絞込み条件と返されたcursorを `snapshot query` に渡すと続きのメタデータを取得します。

- [設定の反映・ログ保持・スクリプト解析](#設定の反映ログ保持スクリプト解析)
- [共有状態の競合と離脱中のローカル変更](#共有状態の競合と離脱中のローカル変更)
- [実行中の処理を取り消す](#実行中の処理を取り消す)
- [動作契約とスナップショットの互換性確認](#動作契約とスナップショットの互換性確認)
- [CoreService の前景起動](#coreservice-の前景起動)
- [共通設定・診断・YAML の読込み](#共通設定診断yaml-の読込み)

## 設定の反映・ログ保持・スクリプト解析

反映前の実差分は `workflow impact --id <workflowId>` と `configuration impact` で取得します。確認付き反映では応答のtokenを `impactToken` として元の候補リビジョンと一緒に送ります。候補や比較元が変わった場合は再照会してください。

共通ログ保持は `log retention`／`log retention-set --body <JSON>`、自由記述の型解析は `script analyze --body <JSON>` です。更新予約は `update schedules|schedule-save|schedule-cancel|schedule-status|schedule-operation` を使用します。予約変更操作のIDと、更新本体のIDを分けて保持してください。

[更新予約](update-process.md)・[スクリプトの解析要求](nodes/scripting.md)に入力形式を示します。

## 共有状態の競合と離脱中のローカル変更

各管理コマンドには、前述のローカル ClientHost `--url` と明示した `--cluster` / `--peer` を指定します。

| コマンド | 入力と用途 |
| --- | --- |
| `conflict list` / `conflict query --body page.json` | 本文を含まない競合一覧。次ページは返された cursor を照会本文へ渡します。 |
| `conflict detail --body target.json` | `{ "id": "競合ID" }` で比較する一件だけを取得します。本文省略は完全な取得成功ではありません。 |
| `conflict resolve --body resolve.json` | `operationId`、`conflictId`、確認した全 `expectedVersions` と `selectedVersion` を指定します。秘密候補は版選択だけにします。 |
| `conflict status --id GUID` | 元の競合解消結果を読み取り、保存・共有・ローカル採用を区別します。 |
| `workflow detached` / `workflow detached-query --body page.json` | ピアに残るローカル版・再参加後の統合待ちを確認します。 |
| `workflow detached-detail --id WORKFLOW_ID` | 変更元・現在の共有版・ローカル版と競合項目を一件取得します。 |
| `workflow detached-resolve --body merge.json` | 確認したローカル版と現在の共有版を固定して、比較選択または明示破棄を送信します。 |
| `workflow detached-status --id GUID` | 元の統合操作の結果だけを確認します。 |

離脱変更の統合本文では `expectedLocalRevision` と `expectedCurrentRevision`（共有版がない場合は `null`）、`currentRevisionConfirmed: true`、項目ごとの `resolutions` を指定します。`discard: true` はローカル保存版の明示破棄であり、既発生のファイル変更・プロセス操作・外部出力を巻き戻す操作ではありません。

`localOnly: true` と共有段階の `notExecuted` はローカル保存・未共有です。CLI はこれをクラスター共有成功へ変更せず、終了コード4と段階結果を返します。結果不明は元の本文と操作IDを保持して読み取り照会し、別IDでの暗黙再送はしません。

K2の端末内内部状態編集はDashboardの「内部状態の確認・端末内編集」を使用します。CLIから外部操作をオフラインキューへ登録するコマンドはありません。

## 実行中の処理を取り消す

`workflow running` で、選択したピアが管理している有限実行を取得します。手動実行に加え、待受ノードから発生して下流を処理中の一件も対象です。待受ノード自身が完了していても、下流の実行を取り消せます。続きがある場合は、応答の `next` を `{ "after": "GUID", "limit": 50 }` の形で `workflow running-query --body page.json` へ渡します。

取消は `workflow cancel --body cancel.json` で要求します。本文には、新しい取消操作の `operationId`、元の実行の `runId`、実行一覧または `workflow run-status` で確認した `ownerInstanceId` を指定します。`--cluster` と `--peer` には、元の実行を受け付けた接続先を指定してください。別のワークフロー実行や、再起動後の同名ピアには適用しません。

取消応答の `outcome` は取消要求の結果です。`requested` は要求を送ったか、`runRunning` と `runOutcome` はその時点で確認できた元の実行状態を示します。取消の受付に成功しても、外部への出力が取り消されたことにはなりません。

`workflow cancel-status --id 取消操作のGUID` は、同じ取消操作の結果だけを照会します。元の実行が終了したかは `workflow run-status --id 元の実行のGUID` で確認します。結果不明の場合は元のIDで照会し、別IDで同じ実行を開始しないでください。

## 動作契約とスナップショットの互換性確認

`workflow compatibility` は、選択したピアの実装に対して、指定ワークフローの保存版・有効版・関連スナップショットを検査します。照会だけを行い、設定変更や復元は行いません。

```powershell
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a workflow compatibility --body samples/cli/compatibility.json
```

本文の `workflowId` を対象に変更してください。`limit` は一ページで確認するスナップショット数で、1～50、既定20です。`nextSnapshot` が返った場合は、その値を `snapshotAfter` に設定して次のページを照会します。

問題があれば、対象範囲・スナップショットID・ノードID・要求版・実装版・確認する設定キー・理由を返します。検出した問題がある場合は終了コード4です。問題が見つかっていなくても、詳細の省略や次ページが残る場合は5となります。問題・省略・続きがいずれもなければ0です。未保存の草稿は `workflow validate` で検証します。他ピアの実装やスナップショットは、そのピアを指定して照会してください。

## CoreService の前景起動

発行済みの CoreService の実行ファイルと TOML を指定してください。CLI は別プロセスとして CoreService を起動し、その終了を待ちます。

```powershell
contents-cli service run --executable "C:\Clutch-CS\Core\Clutch-CS.CoreService.exe" --config "C:\Clutch-CS\configuration\contents.toml"
```

`--executable` には実行ファイルのパスを指定してください。DLL やシェルコマンドは指定できません。Linux では発行した拡張子なしの実行ファイルを指定できます。`--config` には既存の設定ファイルを指定してください。TOML の解析と起動可否の判断は CoreService が行います。このコマンドは OS サービスの登録やリモートシェルの起動には使用しません。終了したプロセスは自動で再起動しません。

CLI は `launchId`・`processId`・`state:started` を出力し、終了後に同じ ID と `state:exited`・`childExitCode` を返します。起動できたことと、ピアが Ready になったことは別です。参加状態は管理コマンドの `status` で確認してください。子プロセスの標準入出力は現在のコンソールを引き継ぐため、CoreService のログと CLI の JSON 行は混在します。

通常終了時の CLI 終了値は子の実終了値です。入力不正で未開始は `2`、OS の起動失敗は `7`。Ctrl+C は通常終了の猶予を 5 秒設け、終了しなければ子プロセス群へ強制終了を要求します。取消後の実終了を確認できた場合は `130`、さらに 10 秒経っても終了を確認できない場合は `5` と `state:unknown` です。`forcedTerminationRequested` は強制終了要求の有無であり、単独では終了保証を意味しません。

未確認時は表示された PID と launch ID を確認し、同じ CoreService を暗黙に再起動しないでください。

## 共通設定・診断・YAML の読込み

以下は `--url`・`--cluster`・`--peer` を明示する管理 API 操作です。

| 操作 | 入力・意味 |
| --- | --- |
| `configuration get` | 保存候補と現在有効な共通設定を取得 |
| `configuration save` | `--body common-save.json`：operationId、settings、expectedRevision、activationPolicy |
| `configuration activate` | `--body common-activate.json`：`operationId`・`revision`・`impactConfirmed` |
| `configuration status` | `--id operation-guid`：元の共通設定操作を読取照会 |
| `resources get` / `resources history` | 現在のリソースと既存の保持履歴 |
| `trace get` | 現在の通常／一時 Trace 方針・読込状態 |
| `trace set` | `--body trace.json`：operationId、workflowId、enabled、expectedVersion、selection |
| `trace status` | `--id operation-guid`：元の Trace 変更結果を照会 |
| `log statistics` | キュー・drop・転送・検索可否などの統計 |
| `workflow parse --yaml definition.yaml` | CoreService の解析処理で YAML をワークフロー定義へ変換。`--yaml -` は標準入力 |
| `workflow validate --yaml definition.yaml` | 選択したピアで解析し、コンパイル時の検証を実施。保存・実行はしない |

```powershell
contents-cli --url http://127.0.0.1:5081 --cluster clutch-cluster --peer peer-a workflow validate --yaml .\workflow.yaml
```

`--yaml` と `--body` は併用できません。`workflow parse` の成功は構文解析の確認であり、実行可能性の確認には `workflow validate` を使います。`--yaml` は CoreService の共通の解析処理でインポート用 JSON も読み取れるため、リポジトリの `.json` サンプルにも使用できます。

インポート用YAML／JSONの `inputs` は生の定数値です。一方、`workflow validate --body definition.json` は管理契約の型付き定義を受け付け、各入力は `{ "kind": "json", "json": 42, "binary": "" }` のような `NodeValue` です。

サンプルを `--body` へ直接渡さず、`workflow parse --yaml` の結果の `response` を定義として使ってください。CLIの出力全体には操作ID等も含まれるため、全体を定義として渡すことはできません。

保存には `{ "definition": <解析した定義>, "expectedRevision": null, "activationPolicy": "auto" }` を本文とし、必要なら `operationId` を付けます。`expectedRevision: null` は新規作成時だけの指定であり、既存の更新では取得した保存版を指定します。有効化や実環境向けの編集後は再検証してください。解析は保存・実行を行いません。

共通設定の保存は Saved／Shared／Snapshot／Activation をそれぞれ確認します。変更の結果が Unknown なら元本文と表示 ID を保持して `configuration status`／`trace status` で照会してください。CLI が新 ID にして変更を再送することはありません。
