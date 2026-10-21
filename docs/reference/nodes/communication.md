# 通信ノード

[ノード一覧](README.md) · [サンプル](../../../samples/generic-communication/README.md)

## 複数PCのNIC・IP割り当て

同じ通信ノードを複数PCで実行する場合は、WorkflowEditorの「NIC・IPの指定方式」を「PCごとにNIC・IPを指定」に切り替え、「PC別のNIC・IPを設定」を開いてください。実行候補の各PCにチェックを入れ、そのPCから取得したNICとIPv4を選択して反映してください。NIC名が同じでも、保存するNIC識別子はPCごとに分離します。実行先が優先度や負荷で切り替わる場合も、候補になる各PCへ割り当てます。

割り当ては安定したホスト IDをキーに保存します。同じPCのピア再起動やコーディネーター交代で他PCのNICへ置き換わることはありません。未検出PCの保存値も残し、NIC識別子・IPv4の手動入力と、PC識別子による行の追加ができます。候補の再取得や実行候補の変更だけでは保存値を変更しません。NICを選び直すとIP欄を空にして再選択を求めます。

```json
{
  "networkBindingMode": "perHost",
  "networkBindings": [
    { "hostId": "host-a", "interfaceId": "NIC-ID-ON-A", "localAddress": "192.0.2.10" },
    { "hostId": "host-b", "interfaceId": "NIC-ID-ON-B", "localAddress": "192.0.2.11" }
  ],
  "localPort": 9000
}
```

未割り当てPCは `network.binding.missing` で開始を拒否します。他PCの割り当てや共通のNIC・IPへの代替は行いません。選択したNIC・IPがそのPCに存在することは実行開始時にも確認し、一台の不備はそのPCの処理へ限定します。同じNIC・IPの一時復旧は従来の待受復旧方針に従い、IP変更やNIC交換は再選択して反映します。全NICを対象にする `0.0.0.0` は使用しません。

この指定はHTTP、OSC、TCP、UDP、Art-Net、WebSocketの送受信、および同じ明示NIC接続を使うAIとRedisで利用できます。「共通のNIC・IPを指定」（方式未指定時の既定）では従来の `interfaceId`／`localAddress` を使います。既存ワークフローの方式や設定値は自動変更しません。PC別方式の新規設定は、対応版のCoreServiceへ更新してから反映してください。

## 外部WebSocket

`websocket.request` は一回の接続を所有するクライアント、`websocket.listen` は継続サーバー、`websocket.reply` は後続ワークフローからの一回の返信です。実行ピアとその `interfaceId`／`localAddress` を明示します。クライアントは `url` にws/wss URLを指定し、必要ならBearer秘密値の `secretId` を指定します。

サーバーは `localPort` と `path` を指定します。

クライアントは任意の `value` を一メッセージ送信し、`awaitReply` がtrue（既定）の場合は一つの完全な応答を待ちます。textは文字列、binaryはバイト列で返し、`messageType` でも区別します。サーバーは同時接続数を `maxConnections`（既定32）で制限し、`replyMode` は `none` / `fixed` / `workflow` から指定できます。

workflowでは待受ノードの `request` をreplyの同名入力へ、返信本文をreplyの `value` へ接続します。返信は元の接続・受付ピアに固定されます。

断片は一メッセージとして組み立て、既定1MiBの `maxBytes` を超えた一接続の失敗で他接続の受付を止めません。`count` はサーバー全体の受付件数で、0は件数上限なしです。待受全体の正常停止と、受付済みメッセージ・返信の期限は別です。既定の一件期限は30秒で、通常停止後も受付済みの返信は終了まで待ちます。送信後の切断・期限で相手側の結果を確認できない場合は `unknown` とし、再接続による再送は行いません。

長寿命クライアントセッションへ独立した複数ノードから送信するAPIや、任意RPCの多重化は提供しません。

`udp-forward.json` は一台のピア上で UDP 受信を別ポートへ転送する例です。`REPLACE_PEER_ID`、`REPLACE_INTERFACE_ID`、`192.0.2.10`、`192.0.2.20` を実環境に置き換えてから有効にしてください。これらは自動探索される既定値ではありません。

Windows では次のコマンドで NIC の固定 ID と現在の IPv4 を確認できます。設定の `interfaceId` には表示名ではなく `Id` を指定します。

```powershell
[System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
  ForEach-Object {
    [pscustomobject]@{
      Id = $_.Id
      Name = $_.Name
      Addresses = ($_.GetIPProperties().UnicastAddresses.Address.IPAddressToString -join ', ')
    }
  }
```

| 種類 | 入力と出力 | 確認する段階 |
| --- | --- | --- |
| `udp.send` / `udp.receive` | `value` のバイト列。送信時の JSON string は UTF-8、それ以外の JSON は JSON 表記 | 送信はローカルソケットの送信成立。受信は一 datagram |
| `osc.udp.send` / `osc.udp.receive` | 下記 OSC envelope | OSC 形式の一 datagram |
| `artnet.send` / `artnet.receive` | 下記 ArtDMX envelope | ArtDMX パケットの送信・形式確認 |
| `tcp.request` / `tcp.listen` | `value` のバイト列と明示 framing | 一フレーム。要求は `awaitReply: true` で一返信も確認 |
| `osc.tcp.request` / `osc.tcp.listen` | OSC envelope、`lengthPrefix` framing | 一 OSC フレーム。任意返信も同じ形式 |
| `http.request` / `http.listen` | 要求は本文 `value`、listen は `{method,path,body}` を `value` に出力 | HTTP 応答確認または要求取得 |

通信ノードは実行PCのNICを使います。サンプルではピアを明示していますが、PC選択ノードによる指定も使用できます。未指定の場合は最優先PCで実行するため、そのPCにあるNICを設定してください。別ピアへの処理の受渡しはワークフローの実行先指定で行い、各通信ノードにはその実行PCのNICと外部の接続先を設定します。

`localAddress` は選択した `interfaceId` に属する IPv4 の指定が必要です。`0.0.0.0` への待受、別 NIC、別 IP への自動 fallback は行いません。NIC 名称変更は固定 ID で維持し、指定 IP の消失中は unavailable にして同じ IP の復帰を待ちます。新 IP を使用するには設定を更新します。

`localPort: 0` は OS が選ぶ空きポートで、Listening 状態の説明に実エンドポイントを表示します。

送信・要求ノードの「このピアで使うポート」(`localPort`) は、加算などの実行結果やコメントノードを経由した数値も接続できます。対象はUDP／OSC／Art-Net送信、TCP／OSC要求、HTTP要求、WebSocket要求、Redisです。一回の実行ごとに送信元ポートを解決し、`0`ならOSが空きポートを自動割当します。未供給・null・小数・範囲外を保存値へ戻したり丸めたりせず、通信開始前に拒否します。NICとローカルIPは明示設定を維持します。

受信・待受ノードのポートは開始時に固定する設定で、動的な実行結果による変更は受け付けません。

送信・HTTP 処理・開始した TCP フレームの `timeoutMs` は既定 30000、`0` または `null` で無期限です。sourceの入力到着待ち自体に既定寿命はなく、件数と受付終了時間が未指定なら継続します。`count` は取得した一件の件数で、不正パケットの一件失敗も含みます（`0`は件数無制限）。

共通の `listenDurationMs` は待受ノードの開始から正常に受付を終了するまでの任意時間で、ノードの `settings` と同じ階層に指定します。画面では［実行］の「受付を終了するまでの時間」から設定します。予定停止は新受付を止め、開始した一件を完了します。明示取消は進行中の処理も解除します。

OSCの `argumentTypes` は画面で選ぶほか、設定端子 `$settings.argumentTypes` に型定義や順序付きの型タグ・型定義配列を接続できます。汎用の `integer` はOSC `h`、`number` は `d`、`byte` は `i`、文字列・一文字は `s`、真偽値は値に応じた `T/F` です。32bit数値やblob、時刻タグなどは明示タグで選択します。`any`・decimal・複合型・省略可能型は引数へ黙って変換しません。

接続値は開始前に固定し、引数の個数と型を編集画面・コンパイル・実行で揃えます。

UDP の `maxBytes` は既定・最大 65507 です。ブロードキャスト送信には `broadcast: true` と宛先 IPv4、宛先ポートを明示します。TCP の `maxBytes` は既定 65507、最大 16777216 です。framing は次のいずれかです。

- `lengthPrefix`: 4 バイト符号付き big-endian 長と本文。OSC over TCP は必ずこの形式です。
- `line`: LF 終端、本文に LF は含められません。CR は本文として保持します。
- `raw`: 一回のソケット read を一分割データとします。送信側の write 単位と受信分割データは一致を保証しません。

TCP listen は接続を順次処理し、各フレームの `reply` 設定があれば固定返信を返します。返信設定は各フレーム受付時の版に固定されます。一クライアントが接続を保持している間、別クライアントは listener の backlog で待ちます。多数同時接続を処理するサーバー用途には現時点の順次処理を前提に構成を判断してください。

OSC 入出力例:

```json
{"address":"/sensor/value","arguments":[{"type":"i","value":42},{"type":"f","value":0.5},{"type":"s","value":"ready"}]}
```

```json
{"timetag":1,"elements":[{"address":"/sensor/value","arguments":[{"type":"T"}]}]}
```

型タグは `i/f/s/b/h/d/t/T/F/N/I` を扱い、blob `b` の値は base64、`t` と bundle timetag は符号なし 64 bit 整数です。4 バイトの zero padding、big-endian 数値、bundle 内長さと最大 16 段の入れ子を検証します。文字列は UTF-8 を使用し、ASCII の標準 OSC 文字列もそのまま扱います。bundle timetag は値として保持し、将来時刻の配送予約は行いません。

未知の型や破損パケットは一件失敗として処理します。

ArtDMX 入出力例:

```json
{"universe":1,"sequence":1,"physical":0,"data":"AP8="}
```

universe は 15 bit Port-Address、データは 2〜512 バイトの偶数長 base64 です。ArtDMX opcode、protocol version、DMX 長を検証します。宛先ポートは一般的な Art-Net 利用時でも明示指定します。ArtPoll、ArtSync、RDM、機器探索はこの DMX コーデックの対象に含めません。

HTTP 要求の主要設定例:

```json
{"interfaceId":"REPLACE_INTERFACE_ID","localAddress":"192.0.2.10","url":"https://example.invalid/graphql","method":"POST","mode":"graphql","secretId":"service-api","timeoutMs":30000,"success":"status","expectedStatus":200}
```

`secretId` は `ISecretStore` の参照で、既定では Bearer Authorization に使用します。API キー形式なら `secretHeader: "X-Api-Key"`、`secretScheme: ""` を指定します。秘密値は実行ごとに一度だけ取得し、本文応答に同じ秘密値がそのまま返された場合も出力では伏せます。

HTTPリクエストの「認証」は `auto`（既定）・`none`・`secret` から選べます。`auto` は保存済みキー名が指定された場合だけ認証するため、従来のワークフローをそのまま使えます。認証不要のAPIでは `none` を指定すると、残っているキー名を参照せず、認証ヘッダーも送りません。`secret` ではキー名が必須です。「認証なし」に切り替えても保存済みのキー名・ヘッダー名・接頭辞は保持されます。

NIC候補には稼働状態を表示します。停止中のNICも将来の復旧用に保存できますが、そのNICが復旧するまで通信は開始しません。VPNアダプターが複数あるPCでは、接続先への経路を持つ稼働中のNICと、そのNICに現在割り当てられたIPv4を指定してください。HTTPの失敗では、NIC停止・アドレス不一致・DNS失敗・到達不能・接続拒否・タイムアウトを区別し、ステータス不一致は受信値と期待値を示します。送信開始後に結果が確認できない要求は結果不明のまま扱い、自動再送しません。

HTTP 成功条件は `response`（応答本文取得）、`status`（`expectedStatus`、既定 200）、`body`（status と `expectedBody` の完全一致）から明示できます。`maxBytes` は既定 1048576、最大 16777216。HTTP 要求は自動 redirect、proxy、再試行を使用しません。

未登録のリソースだけをForEachで読み飛ばす場合は、HTTPの成功条件を `response` にし、`statusCode` を[C#の判定スクリプト](../../../samples/generic-communication/optional-http-status.csx)へ渡します。200はtrue、404はfalseを返し、それ以外はエラーにします。

その値をIfノードへ渡し、true側を本文解析のStartSignal、false側をForEach Endの `continue` へ接続します。404の要素は結果へ収集せず、登録済みの要素だけを集約します。通信失敗や401・500などを未登録として隠しません。HTTP応答をすべて取得成功と見なしたまま本文解析へ直結しないでください。

GraphQL の要求 `value` は `{"query":"{ value }","variables":{},"operationName":null}` のような envelope です。照会は必須で、variables と operationName は省略できます。GraphQL の応答はデータの存在と errors がないことを検証します。

HTTP listen は `mode`、`path`、`responseStatus`、`responseBody` で固定応答を設定する独立 Kestrel 待受です。GraphQL の文法解釈、スキーマに対するフィールド解決、resolver 実行を行う general エンジンは含みません。必要な問い合わせ処理は外部 GraphQL サーバーへ送ります。

UDP/TCP の送信成立だけから相手機器の実動作成功とは判断しません。送信後のタイムアウト、切断で完了を確認できない場合は unknown とし、自動再送しません。HTTP status 不一致や確認できた不正入力は failure として区別します。

接続線形式の一次資料: [OSC 1.0](https://opensoundcontrol.stanford.edu/spec-1_0.html)、[Art-Net 4](https://art-net.org.uk/downloads/art-net.pdf)。Windows ループバック上の実通信とコーデックの境界は単体テストで確認しています。実 NIC の抜き差し、DHCP 更新、別 OS、照明機器、外部 HTTPS/API の実接続は各環境で確認してください。

## ワークフローで決めるサーバー応答

`http.listen` と `tcp.listen`（OSCを含む）の `replyMode: "workflow"` は、従来の `value` に加え `request` ポートへ `{ownerPeer,id}` を出力します。受付時のピア実体に結び付くopaque ハンドルで、期限内に一回だけ応答できます。

後続のparse・condition・状態などの結果とこのハンドルを `http.reply` または `tcp.reply` の `value` / `request` 入力へ配線します。

返信ノードの実行先には `target: {"scope":"One","sourcePeer":true}` を明示すると、実際のsource入力発生ピアへ実行を固定できます。手動実行などsource ピアがない場合や別ピアのハンドルは未実行となり、別ピアへ暗黙代替しません。同じピアを明示指定する構成も使えます。

HTTP返信の `value` は `{"statusCode":200,"contentType":"application/json","body":{"answer":42}}` の形です。`body` が文字列ならUTF-8本文、それ以外ならJSONとして返します。`bodyBase64` でバイナリを指定することもできます。

`value` 自体がバイナリならステータス200・`application/octet-stream`、単なる文字列なら200・`text/plain`で返します。TCP返信は、受付時のraw/OSC形式とフレームの区切り方を維持します。

返信待ちの期限はsourceの `timeoutMs`（既定30秒、0は明示無期限）です。期限切れ・返信前切断・二重返信・誤ったピアはNotExecuted、送信開始後に確認できない切断や取消はUnknownとして扱います。ローカル書込みが確認できた後の取消は成功を変更しません。受信済みsourceの成功は、返信の成功や失敗で変更しません。

HTTP無返信の期限切れは504、返信処理が失敗して未送信なら500、同時待ちハンドル上限は503です。TCPの無返信期限切れではその接続を閉じ、listenerは継続します。通常停止時は受付済み返信をdrainし、無期限設定の場合も明示取消以外で途中終了しません。registryの既定上限は1024で、ホストから `CommunicationReplyRegistry(maximumPending)` を生成処理へ注入して変更できます。

`http-workflow-echo.json` は受信本文を後続replyから返す例です。全listenerをAll対象にする場合も、返信先はSourcePeerにより入力が発生したピアだけになります。固定応答を使う既存設定の既定は `replyMode: "fixed"` のままです。

## 値ノードから型付きパラメーターを組み立てる

`http-typed-input.json` は、`age` を必須の整数、`tags` を省略可能な文字列配列として受け付ける例です。ピア ID、Interface ID、IPv4 を実環境に置き換えて検証し、有効にします。`GET /person?age=25&tags=art&tags=light` は、型付きの `{"age":25,"tags":["art","light"]}` を後続の返信へ渡します。

`age=abc`、age の欠落や重複、定義していない項目は 400 です。GET 以外は 405 を返します。

型ノード → Key-Value ノード → オブジェクト型の順で、パラメーターの名前と型を構築しています。型も JSON 値として流れるので、同じ Key-Value／Array ノードを実データの構築にも使えます。`bodyType` へ同じ型を接続すると、本文の JSON がその型と一致するか検証します。照会は文字列から明示変換しますが、本文の `"25"` を暗黙に整数へ変換しません。型を指定しない既存の本文出力は文字列のままです。

Interface ID ノードの出力は `$settings.interfaceId` へ、HTTP メソッドノードは `method` 入力へ接続します。設定接続は待受の開始前に解決され、元の保存設定を変更しません。通信の受信結果など、実行するまで値が決まらないノードを静的な待受設定へ接続した場合は、検証エラーとして修正できます。

## HTTP・WebSocketの共有待受

同じCoreServiceプロセス内の同じNIC・IP・ポートでは、HTTP受信とWebSocketサーバーが一つの待受を共有できます。`/api/input`、`/events` など、異なるパスを指定します。パスは大文字小文字を区別した完全一致で、未登録のパスには404を返します。ポート0は個別の動的ポートです。

同じパスは、HTTPメソッドが異なる場合やHTTP／WebSocketをまたぐ場合も二重登録できません。後から登録した待受ノードを `http.endpoint.duplicate` で終了し、既存の受付は継続します。一つの待受ノードを停止しても他のパスは維持します。正常停止ではそのパスの受付済み処理を待ち、最後の利用が終わってから共有ホストを解放します。

## HTTP BodyのMIME自動判定と明示形式

新しく追加したHTTPサーバーノードは「受信Body形式」の **Content-Typeから自動判定** を初期値にします。既存定義で`bodyFormat`がない場合は「既存方式（互換）」のまま動かし、本文の扱いを勝手に変更しません。明示JSON・その他（テキスト）・Binaryを選ぶこともできます。優先順位は **明示形式 → 自動で認識できたContent-Type → 判定不能時の指定形式** です。

- `application/json`・`application/*+json`など末尾`+json`：本文そのものをJSONとして解析します。「JSON本文全体のスキーマ」は、その本文のObject・配列・文字列・数値などルート全体に対するJSON Schemaです。スキーマ文書自体をPOSTする指定ではありません。不正なJSON・スキーマ不適合はHTTP 400となり、成功経路へ通知しません。
- `text/*`：UTF-8の本文文字列をそのまま渡し、JSONスキーマ・本文の手動型は適用しません。
- `image/*`・`audio/*`・`video/*`・`font/*`・`application/octet-stream`・PDF・ZIP・GZIP：本文を文字列化せずBinaryとして渡します。受信したバイト列を維持します。
- MIMEが欠ける・壊れている・独自型などで判定できない場合：「Content-Type判定不能時の本文形式」でJSON／テキスト／Binaryを指定します。初期値はBinaryです。

明示Binaryまたは判定不能時のBinaryには、`image/png`・`audio/wav`・`application/octet-stream`などの「明示／フォールバック時のBinary MIMEタイプ」を指定できます。自動判定でMIMEを認識できた場合は受信MIMEを使い、明示Binaryでは指定MIMEを優先します。MIMEの指定はファイル内部の検査や再エンコードではありません。内部フィールドを取り出す場合は既存のバイナリ解析ノードへ渡します。

multipartは個別ファイルへ自動分解せず、未認識時のフォールバックに従って本文全体を扱います。

出力`body`が実際の本文、`bodyContentType`が判定・指定後のMIMEです。新しい本文形式を選んだ受信情報Objectには受信Content-Typeとバイト数も含めます。互換方式では受信情報の従来の形を維持します。Binary時は受信情報の`body`をnullとし、Base64で二重に本文を保持しません。バイト列は独立した`body`出力を使用します。既存の受信サイズ上限を維持し、GraphQLを明示Binaryで扱う指定は拒否します。

これらはPOST専用ではなく、HTTP受信の全メソッドで共通です。

自動判定では受信前に本文形式を断定できないため、本文全体のポートは任意型です。JSONスキーマで宣言した項目は、JSONだった場合の省略可能な候補として個別出力へ表示します。テキスト・Binary受信時にはその個別項目は供給されません。常にJSONとして受ける入口には明示JSONを選択してください。応答のContent-Type設定とは別の指定です。

「JSON解析」「YAML解析」「TOML解析」ノードにも、自動／JSONスキーマ／手動型の指定方式を提供します。スキーマは**解析後のデータ全体**に適用し、出力項目表示と実行時検証に同じ定義を使います。YAML/TOMLにも、データの構造を表すJSON Schemaを指定します。未指定なら既存の解析動作を維持します。

## OSCパケットを組み立て

OSCパケットから各引数を取り出す場合は、後述の「OSCパケットを解析」を使用します。

「通信」の **OSCパケットを組み立て** (`osc.packet`) は、`OSCアドレス（Path）` と0個以上の引数から、既存のOSC送信ノードへ渡せるObjectを作ります。このノード自身は通信しません。ここでのPathは `/1/start` などOSCのアドレスで、文字列とPath型を入力できます。ファイルパス用の区切り変換は行いません。元パケットがない場合、アドレスは必須で、`/`から始まり、終端文字を含まない文字列を指定します。

既存のOSCパケットObjectは「元のOSCパケット」入力へ接続できます。保存済みワークフローの汎用引数口に接続されたパケットも引き続き受け取ります。この場合、アドレス入力を省略すれば元パケットのアドレスを使い、入力されていればそのアドレスを優先して上書きします。引数の順序と型タグは保持し、元の入力Objectは変更しません。空文字列や不正なアドレスを明示した場合は、元のアドレスへ戻さず失敗します。Bundle全体のアドレス置換、パケットと別の引数の混在は受け付けません。

引数口は2つから始まり、両方を接続・入力すると次の口が増えます。未接続・未入力の口は無視し、番号順で組み立てます。接続済みで値が未供給の口を、省略した引数として実行することはありません。引数を一つも指定しなければ、死活通知などに使える空引数パケットを作ります。明示したnullは省略と区別し、OSCの`N`引数にします。

一つの口だけに配列を接続した場合は、その要素を順序通り引数列へ展開します。空配列も引数なしになります。配列と別の引数の混在、入れ子配列、通常のObjectは変換不能として失敗します。型タグ付きの `{ "type": "i", "value": 1 }` のような引数Objectは受け付けるため、OSC受信ノードの「型タグ付き引数」配列も接続できます。

通常の値は、32bit範囲内の整数を`i`、範囲外のInt64整数を`h`、小数・指数表記の数値を`d`、文字列・Pathを`s`、Binaryを`b`、真偽値を`T/F`、nullを`N`へ変換します。数値に見える文字列も文字列のままです。明示した`int64`型（定数・型変換・C#の`long`／`System.Int64`・OSCの`h`受信・Binary解析の`int64`）は小さい値でも`h`を使います。

`{"type":"h","value":1}`や`f`（32bit浮動小数点）などの型タグ付き引数でも精度を指定できます。明示した`i`の32bit範囲外はエラーとし、丸めません。数値範囲、文字列の終端文字、BinaryのBase64、型と値の不一致を実際のOSC符号化と同じ処理で検証します。引数は最大64個、組み立てサイズは入力換算と符号化後の両方で1MiB以内です。送信先のパケットサイズ上限は別途適用します。

出力の「OSCパケット」をOSC（UDP）送信またはOSC（TCP）要求の「OSCパケット」入力へ接続します。送信側の「OSC引数の型」は未指定のパケット方式を使い、送信側のアドレス上書きが不要ならアドレス欄を空にします。NIC・接続先IP・ポートは送信ノード側で指定します。OSCのUDP送信・TCP要求では、通常のアドレス入力と重なる未使用の設定接続口を表示しません。既存の設定欄への配線はそのまま表示・実行し、設定値の直接編集も維持します。

## OSCパケットを解析

「通信」の **OSCパケットを解析** (`osc.parse`) は、OSC受信・組み立てノードが出すパケットObject、またはUDP受信などの生のOSCバイト列を解析します。必須入力「OSCパケット／バイト列」へ接続してください。通信や待受は行わず、TCPの長さヘッダーを含まない一つのOSCパケットを扱います。入力上限は1MiBです。

「OSC引数の型」はOSC受信ノードと同じ操作です。引数の順序に型を指定すると、「引数1」「引数2」などの型付き出力が現れます。型定義ノードや型の配列を設定口へ接続する方法も共通です。例えば `i, s, b` を指定したパケットからは、32ビット整数・文字列・バイナリをそれぞれ取り出せます。

- 共通出力は「OSCパケット」「OSCアドレス」「型タグ付き引数」です。引数型を指定した場合は個別の引数出力も加わります。
- 型未指定はパケット形式です。「引数なし」を選ぶと引数0個を検証します。個数・型タグの不一致は失敗となり、部分出力や別型への自動変換を行いません。真偽値の型はT／Fの両方を受け取り、trueのみ・falseのみも指定できます。
- Object入力も送受信と共通のOSC コーデックで検証し、OSCの数値精度・型タグに合わせて解釈します。元の入力は変更しません。バイト列では長さ・終端・padding・型タグを検証し、不正な内容を推測で補いません。
- Bundleは型未指定の場合に「OSCパケット」だけへ出力し、時刻タグと入れ子構造を保持します。引数型を指定した場合は失敗です。Bundleの分解、時刻待ち、メッセージの実行は行いません。

形式は既存のOSC送受信と共通で、[Open Sound Control 1.0仕様](https://opensoundcontrol.stanford.edu/spec-1_0.html)のパケット・型タグ・Bundleの区別を使います。
