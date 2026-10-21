# データ・条件・時刻ノード

[ノード一覧](README.md) · [サンプル](../../../samples/generic-data/README.md)

データの作成・変換、集合操作、条件分岐、状態の保存、日時の取得に使うノードをまとめています。

## 配列の要素数を取得する

「配列の要素数（Count）」(`data.count`) は、入力した配列の直下の要素数を整数で返します。`[]` は `0`、`[null,false,0]` は `3`、`[[1,2],[3]]` は `2` です。文字列・Object・null・バイト列は配列へ暗黙変換せず失敗にします。HTTP応答の本文をJSON解析した後の配列や、既存の「配列を作成」ノードの出力を接続できます。

## 現在の日付・時刻

「現在の日付・時刻を取得」(`scheduling.now`) は、実行時の現在時刻を日時文字列として返します。`timeZoneId` は既定 `UTC`、日本時間には `Asia/Tokyo` または `Tokyo Standard Time` を指定します。クラスターやPCの既定タイムゾーンに暗黙依存しません。`format` の既定は `yyyy-MM-dd HH:mm:ss.fff` で、両設定には設定入力端子からも値を接続できます。

取得は保存・コンパイル時には行わず、一回のノード実行で一つの時刻を固定します。

書式は地域設定に依存しない[.NET日時書式](https://learn.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings)を使います。年・日の別表記として、引用・エスケープ外の `YYYY`・`YY`・`DD` も受け付けます。例: `HH:mm:ss.fff-YYYY/MM/DD` → `03:04:05.678-2025/01/02`。

`MM` は月、`mm` は分です。`'YYYY'` のような引用文字列はそのまま出力します。未知のタイムゾーンや不正な書式は設定エラーとし、UTCや別書式へ黙って置き換えません。

## Key-Value配列から値を取得

**Keyで値を取得**（`data.keyValue.get`）は、`value`にKey-Value配列、`key`に検索する文字列、`defaultValue`に見つからない場合の値を受け取ります。`value`出力は選択した値をそのまま返します。整数・文字列・Object等を強制変換せず、既存のPath型情報も保持します。

「形式」は次の3種類です。

- **自動判別（既定）**：要素ごとに判定します。`key`と`value`、または`Key`と`Value`の2項目だけを持つObjectはペア、それ以外はプロパティを検索するObjectとして扱います。両形式を同じ配列に混在できます。
- **Object配列**：`[{"A":1},{"B":2}]`のような配列です。既存の`value.keyValue`ノードを`value.array`で並べた出力をそのまま接続できます。一要素に複数のプロパティがある場合も対応します。文字どおり`key`／`value`というプロパティ名を検索したい場合は、この形式を明示してください。
- **key/valueペア配列**：`[{"key":"A","value":1},{"key":"B","value":2}]`のような配列です。.NETの既定表記`Key`／`Value`にも対応します。各要素はこの2項目だけを持ち、キーは文字列で指定します。

キーは大文字・小文字を区別した完全一致です。検索キーが複数見つかる場合や配列形式が不正な場合は失敗します。値が`null`・`false`・`0`・空文字列の場合も「見つかった値」として返し、デフォルト値へ置き換えません。キーがなくデフォルト値も未接続の場合は`null`です。

入力配列とキーが既知なら選択値の型を推論します。キーが動的なら既知の候補とデフォルト値を含めた共通型を使い、数値と文字列の混在など確定できない場合は`any`と表示します。外部入力の実値が未確定な場合も、明示形式と宣言型から保証できる範囲に限定します。

## 集合の結合・グループ化・展開・分割

| ノード | 入力・設定 | 出力 |
| --- | --- | --- |
| `data.join` | left/right配列、leftPath/rightPath、comparison、kind（inner/left/right/full） | `{left,right}` の行配列。未対応側はnull |
| `data.group` | value配列、keyを選ぶJSON Pointerのpath | `{key,items}` の配列。keyの初出順と要素順を維持 |
| `data.flatten` | value配列、depth（既定1、最大32） | 指定深さだけ平坦化した配列 |
| `data.expand` | object配列、展開する配列field、keepEmpty | 他の項目を維持し、配列要素ごとに作ったobject配列 |
| `data.split` | value配列、path、comparison、expected入力 | matched/rejected/indeterminateの三配列 |

pathはJSON Pointerです。空文字は要素全体を示します。比較は既存Conditionと同じequal/notEqual/less/lessOrEqual/greater/greaterOrEqualを使い、数値と文字列を暗黙に混ぜません。joinは重複一致をすべて出し、左側の順と右側の一致順を保ちます。null・欠落の一致は `matchNull` を明示した場合だけです。groupは構造比較を使い、欠落とnullを同じkeyにまとめます。

splitは比較不能をrejectedへ隠さずindeterminateへ出します。

既定は入力・出力10,000要素、16MiB、比較・巡回100万ステップです。`maxItems`、`maxBytes`、`maxWork` で有限範囲を選び、超過時は部分配列を成功として返しません。空集合は空結果です。配列の各要素に別ノードを実行する用途は、引き続き[ForEach／Parallel ForEach](iteration.md)を使用します。

[math-and-logic.json](../../../samples/generic-data/math-and-logic.json) は、累乗→累乗根→比較→論理演算をつないだ手動実行用の例です。初期状態では無効なので、有効化して保存してから実行します。結果はpower=8、root=2、compare/equal/not-equal/and/or=true、not=false、nand=-4、nor=-4、xor=4、ands=1、ors=7です。

外部への送信やファイル変更はありません。

論理ノードは全入力を検証し、真偽値と整数の混在・小数・欠落を正常な真偽値へ置き換えません。整数はJSONの数値を丸めずにInt64の範囲で解釈します。JSON自体はバイト／Int32等のCLR整数幅を保持しないため、ビット演算の幅は64ビットです。Compareは数値表記4096文字以内、記述した十進指数と正規化後の指数がともにInt32の範囲で、整数・decimalの桁を保って比較します。上流の処理を短絡実行する機能はありません。

`transform-and-observe.yaml` は timer → JSON解析 → 項目構築 → 条件 → 明示ログ記録の構成例です。製品共通設定のピア/クラスターと保存先を設定し、ワークフローを有効化して使用します。実行先未指定の場所非依存ノードはコーディネーターで動きます。サンプルは初期状態では無効です。YAML/JSON のインポートでは、定数入力を `inputs.value` などへ直接指定します。

例えば文字列は `value: '42'`、整数は `value: 42` です。`kind`／`json` で包むと、そのオブジェクト全体が入力データになります。

| 種類 | 主な設定と入出力 |
| --- | --- |
| `data.{json,yaml,toml,csv,text,binary}.{parse,serialize}` | 必須入力 `value`。CSVはヘッダーを特別扱いしない行・セル配列。TextはUTF8/文字列。TOMLのnullや非文字列YAMLキー等、対象形式で表現できないものは失敗 |
| `data.extract` | `mode: pointer/row/cell/line/regex/binary`。JSON Pointer `path`、0基点 `row`/`column`、`pattern`/`group`、`offset`/`length`/`fields`。`value` と `count` を出力。抽出対象なしは正常null、配列Countはnull要素も数える |
| `data.convert` | `value` の非nullスカラーを `targetType: string/path/number/integer/int64/boolean` へ明示変換。numberは有限binary64、integerは正確なInt64範囲の通常整数、int64は64bit精度を明示した整数。OSCでは通常整数を32bit範囲内ならi、明示int64を常にhとして扱います。配列・オブジェクト・変換不能・範囲外は失敗 |
| `data.transform` | `operation: create/set/remove/rename/select/concat/map`。`create.fields` は固定JSON値または一項目 `{$path: /source}`。`set.name` と `replacement` 入力で一項目変更。`names` は削除/選択項目。`rename.name/to`、`concat.values/separator`、`map.mapping: [{from: ..., to: ...}]` |
| `control.condition` | 元データ `value`、観測した `comparison`、任意 `expected` 入力。設定 `operation: equal/notEqual/less/lessOrEqual/greater/greaterOrEqual/regex/boolean/timeWindow`。比較値未入力は `indeterminate`。`true`/`false`/`indeterminate` の選択経路だけに元値を出力 |
| `control.pass/delay` | 元値通過。delayは `milliseconds` の間待ちます。緊急取消トークンで待機を解除 |
| `math.add/subtract/multiply/divide` | JSON数値 `left/right` の四則演算。有限binary64の `value` を出力。文字列の暗黙変換、ゼロ除算、非有限の結果は拒否 |
| `math.compare` | Compare／大小比較。数値 `left/right` を設定 `operation: less/lessOrEqual/greater/greaterOrEqual` で比較（既定less）。十進数値を丸めず、`value` に真偽値を出力 |
| `math.equal/notEqual` | Is Equal／Is Not Equal。一致・不一致を `value` の真偽値で返します。`left/right` のJSON内容またはバイト列を比較。文字の大小・配列順序を区別し、数値と文字列を暗黙変換しない |
| `math.power/root` | Powerは `base` の `exponent` 乗。Rootは `value` の `degree` 乗根（省略時2）。次数は1〜Int32.MaxValueの整数。値・累乗指数・結果は有限binary64で丸めがあります。負数の偶数次ルート・0の負指数・実数外・範囲外は失敗。0の0乗は1 |
| `math.and/or/nand/nor/xor` | `left/right` の2入力。真偽値同士は論理演算、整数同士は符号付き64ビットのビット演算。NAND/NORはAND/ORの結果を反転 |
| `math.not` | `value` の1入力。真偽値は論理否定、整数は符号付き64ビットの各ビットを反転。入力に対応する真偽値または整数を `value` に出力 |
| `math.ands/ors` | `value` に同じ種類の真偽値または整数の配列を2〜4096個渡し、全要素のAND/ORを1つの値で返します。Arrayノードの出力も接続可能 |
| `control.publish/hook` | 発行の `name` と `value` を共通イベントへ渡します。hookは任意 `name`/`workflow` フィルター、`count` で予定終了 |
| `control.log` | `level`、固定 `message`、`personal` と `value` 入力を共通ログ境界へ渡します。secret/個人情報処理は共通ログ実装が担当 |
| `state.get/set/compareExchange/delete/dedupe` | `scope: workflow/peer` 必須。`key` は設定または入力。ワークフローはクラスター共有範囲、ピアはピア固有範囲。set/deleteは読取時の版でCASし競合なら `conflict`。明示CASは `expectedVersion`。dedupeは原子的に確保し `success`/`duplicate`。明示 `ttlMilliseconds` なしでは期限失効しない |
| `scheduling.timer` | `mode: interval/once/daily/weekdays`。intervalは `intervalMilliseconds`、onceは `at`、daily/weekdaysは `time`、weekdaysは日曜0〜土曜6。`start/end`、`includeDates/excludeDates`（yyyy-MM-dd）、`count` を指定可能 |

時間指定とtimeWindowはクラスター共通タイムゾーンを使用し、ノード固有timezoneは受け付けません。夏時間の曖昧・欠落時刻は失敗として示します。曜日の追加日は指定曜日外にも実行し、除外日は追加日より優先します。未実行回が複数あれば最新の対象回だけを残し、既定は遅延実行です。`missed: skip/within` と `toleranceMilliseconds` で遅延超過時に見送りへ変更できます（skip未指定許容値は1000ms）。

通常の予定終了と緊急取消は結果を分けます。

時計品質の観測値を `clockValid` へ供給できます。未入力・型不一致は品質不明、falseは明示不良、trueは確認済みとして扱います。既定は警告して利用できる時計で継続し、`timeQualityPolicy: stop` を明示した場合だけ品質回復まで時間契機を保留します。時刻品質自体の測定をこのノードが推測することはありません。

Binaryの `fields` は `[{name, type, offset, length?, endian?}]` です。型は `uint8/int8/uint16/int16/uint32/int32/uint64/int64/float32/float64/utf8/bytes`、既定バイト順はlittle、bigを指定できます。bytes/utf8は明示lengthが必要です。

構造化されたbytesフィールドは0〜255の整数配列、全体バイト列はNodeValueのバイナリとして維持します。フィールド名・領域重複、不正範囲、UTF8長不一致を拒否します。

各ノードは同じJSON Schema検証を利用します。未知の設定キーは誤記を黙って既定にせず拒否します。正規表現の実行上限は1秒です。JSON互換構造の最大深さは64で、上限超過や循環したYAML参照は切り詰めず失敗にします。集合操作は以下の専用ノードを使用します。自由記述は独立したscriptノードで扱い、基本変換ノード内では任意コードを評価しません。

静的設定の構築には `value.constant`／`type`／`array`／`keyValue`／`interface`／`peer`／`file`／`directory` と `http.method` を使用します。`$settings.<設定名>` へ接続しても元の保存設定は保持します。Mathや変換ノードは値を扱いますが、静的設定用の純粋な値ノードではありません。ピア・パス選択は文字列を作るだけで、処理ノードの実行先を変更しません。

実行先は[PC選択](../workflow-editor.md#実行pcを選択する)で指定します。

配列の要素ごとに既存ノードを実行する場合は[ForEach／Parallel ForEach](iteration.md)を使用します。ANDS／ORSの配列集約とは用途が異なります。

## テキスト処理

「データ変換」に **Regex** (`text.regex`)、**Match** (`text.match`)、**SubString** (`text.substring`) を用意しています。対象テキストと正規表現、SubStringの開始位置・長さは他ノードから配線できます。これらに非同期処理用の汎用完了信号は付きません。

RegexとMatchは.NETの正規表現を使い、JavaScriptの正規表現へ置き換えません。名前付きグループ・番号付きグループ・繰り返しのキャプチャは[.NETのグループ規則](https://learn.microsoft.com/en-us/dotnet/standard/base-types/grouping-constructs-in-regular-expressions)に従います。

- Regexの既定は「すべての一致」です。一致全体（グループ0）と各グループを、それぞれ配列の出力端子に表示します。名前付きグループはその名前で表示します。同じグループの全Captureを一致順に並べるため、省略されたグループを含むパターンでは、異なるグループの配列長が同じとは限りません。
- 「最初の一致のみ」では、一回だけキャプチャされると判断できるグループは文字列、不一致はnullです。繰り返し・重複名による複数キャプチャは配列を維持します。inline options・条件分岐・バランシング等で単一と証明できないグループも配列にし、最後のCaptureだけへ切り捨てません。パターン自体は.NETで実行します。
- `(?<name>\w+)-(?<age>\d+)`へ`alice-12 bob-34`を渡すと、全一致モードの`name`は`["alice","bob"]`、`age`は`["12","34"]`です。最初のみでは`"alice"`・`"12"`になります。「抽出グループ」から全体のObject、「一致件数」から件数も取得できます。
- Matchは一箇所でも正規表現に一致すればtrue、不一致ならfalseです。不正パターンやタイムアウトをfalseには置き換えません。
- SubStringの開始位置は0始まりのUTF-16単位です。長さ省略／nullは末尾まで、長さ0は空文字列です。負数・範囲外・整数以外、サロゲートペアの途中を切って壊れたUnicodeになる範囲はエラーです。

Regex／Matchのパターンは最大8,192文字・128グループ、入力は1,048,576 UTF-16単位までです。全一致は最大4,096件、全グループ合計16,384キャプチャ・4,194,304 UTF-16単位までとし、超過時は途中結果を成功として返しません。照合期限は既定1,000ms、1〜10,000msで指定できます。名前付き出力の事前表示には、直接入力または既知の定数から取得できるパターンが必要です。

## オブジェクト配列・型指定・個別項目の出力

ForEachとParallel ForEachは任意のオブジェクトを含む配列を反復できます。「要素型の指定方式」では次の 3 種類を選択できます。

- **入力から自動推測**：直接入力や接続元の既知の型から項目名と型を推測します。既定の方式です。実行時の受信まで形が分からない場合は型を断定しません。
- **JSON Schema入力**：配列全体ではなく、**配列の一要素**に対するJSON Schemaを指定します。型だけでなく、必須項目、数値範囲、文字列長、追加項目の許否なども実行時に検証します。
- **手動設定**：「型・項目を編集」から項目名・型・省略／nullの許可を編集します。配列やオブジェクトの入れ子にも対応します。手動定義の省略許可はnullも許可します。JSON Schema方式では元スキーマの`required`とnullの許否をそれぞれ検証します。

例えば入力が `[{"item-id":1,"quantity":10,"name":"ケーブル"}]` なら、要素全体を表す `item` や反復位置を表す `index` などに加え、`item-id`（品目 ID）・`quantity`（数量）・`name`（品名）を個別の出力端子として表示します。`item` からパスを指定して値を取り出すこともできます。この要素を JSON Schema で定義する例は次のとおりです。

```json
{
  "type": "object",
  "properties": {
    "item-id": { "type": "integer" },
    "quantity": { "type": "integer", "minimum": 0 },
    "name": { "type": "string" }
  },
  "required": ["item-id", "quantity", "name"],
  "additionalProperties": false
}
```

手動・スキーマ方式は反復開始前に配列全体を検証します。後半の要素だけが不適合でも、反復内の処理を一度も開始せず失敗理由を返します。自動方式では入力を勝手に変換しません。オブジェクト間で項目が欠ける場合は省略可能と推測し、同じ項目の型が異なる場合は任意型として扱います。型が分からない実行時入力に対して出力項目を事前に用意したい場合は、スキーマか手動方式を使用してください。

同じ指定方式を「配列を作成」の要素型、HTTP受信のクエリー型・本文型にも提供します。既存の型入力端子（定数の型、配列の要素型など）でも、自動／既定・JSON Schema・手動編集を選べます。通常入力の型定義が接続・直接指定されている場合は、それが設定欄の方式より優先されます。HTTP本文は、方式を保存していない既存定義では従来の文字列を維持し、明示的に自動方式を選んだ場合は有効なJSON本文をJSON値として扱います。

個別項目出力はForEach以外の型が分かるオブジェクト出力（定数、キー値構築、HTTP本文など）にも共通です。元のオブジェクト出力は残します。ハイフン、日本語、`/`・`~`、既存出力名と同名の項目も区別します。一階層を最大128項目まで表示し、入れ子は既存のパス指定で取得できます。項目名の変更や削除によって既存配線が不適合になった場合は検証で確認してください。

スキーマはDraft 2020-12を既定として検証し、同じ文書内の`$defs`／`$ref`を利用できます。外部URL・ファイルの参照は自動取得しません。スキーマは最大65,536文字です。複合条件から型を断定できない場合は出力型の表示を任意型に留めますが、実行時には元の条件全体を検証します。OSCのプロトコル型タグ、C#／Pythonのコード契約解析は、それぞれの既存仕様を維持します。

検証にはMITライセンスのJsonSchema.Net 8.0.5を使用し、ライブラリは実行側に限定しています。配布条件は同梱のOSS Licenseに収録しています。

## 結合（Concat）ノード

`Data conversion` の「結合（Concat）」(`data.concat`) は、文字列どうし、または同じ要素型の配列どうしを結合します。入力端子は最初に二つ表示し、配線または直接入力で全て埋まると次の入力端子を一つ表示します（最大64入力）。入力を外した場合は途中の空き口を維持し、残る接続の番号を詰めません。空いている口は無視し、値のある口を番号順に結合します。供給値が一つならその値、ゼロなら入力不足のエラーになります。

「結合文字列」(`separator`) は任意の文字列入力で、未接続またはnullなら空文字です。例えば `A`、`B`、`C` に `, ` を指定すると `A, B, C` となり、先頭・末尾へは付けません。空文字の要素も一つの要素として扱い、未接続口だけを除きます。入力が一つなら区切りは挿入しません。区切りの繰返しも1 MiBのサイズ上限に含めます。配列の結合は区切りなしで行い、空でない結合文字列と配列の組合せはエラーです。

Pathだけの結合は既定では従来のパス結合になり、空でない結合文字列を指定すると通常のStringとして連結します。

例えば、入力1=`"ab"`・入力2=`"cd"`・入力3=`"ef"`なら`"abcdef"`、入力1=`[1,2]`・入力2=`[3]`なら`[1,2,3]`です。接続を作った時刻や辞書の並び順は使いません。区切り文字の自動挿入や、文字列と数値の暗黙変換は行いません。

配列の要素型は既存のJSON型体系で検証します。整数・小数は数値として結合でき、元の数値表現と精度を維持します。文字列と数値、配列と文字列などは混在できません。入れ子配列やオブジェクトは内部の型・プロパティ構造も一致する必要があり、空配列は他の入力の要素型を制約しません。空文字・空配列どうしの結合も可能です。結合する最上位の配列要素は合計4096件まで、入力JSONのUTF-8サイズ合計は1 MiBまでとし、上限超過時に部分結果は出しません。

出力型は接続元から推測し、値が確定できる場合は結合結果も静的プレビューに表示します。同じ純粋な結合処理を設定入力・通常実行・プレビューで共用します。

## オブジェクトを展開・If-Start・IfNot-Start

「データ変換」の **オブジェクトを展開** (`data.object.expand`) は、左側で解析済みのObjectを受け取り、右側へ一階層のプロパティを個別の出力端子として並べます。JSON／YAML／TOMLの解析結果、HTTPの本文・クエリ、ForEachの要素や`details`などを接続できます。文字列の解析は既存の各形式の解析ノードで先に行います。Object全体の出力も残し、入れ子のObjectは次の展開ノードにつないで取り出せます。

型の指定方式は「入力から自動推測」「JSONスキーマ」「手動設定」の3種類です。自動では接続元の既知の型・固定値を使います。実行前に構造が分からない入力は項目を断定しないため、スキーマまたは手動で用意してください。スキーマ／手動方式は表示だけでなく、実行時にも元の入力を変換せず検証します。個別出力は最大128項目、プロパティ名のハイフン・日本語・`/`・`~`も区別します。

通常データの欠けた項目は既存のプロパティ抽出と同じnull、必須の設定値として取り出す場合は未供給として扱います。

例えば、種類を表す `kind` と、その詳細を表す `details` を持つ配列を処理できます。

```json
{
  "items": [
    { "kind": "message", "details": { "subject": "処理完了", "text": "ファイルを保存しました。" } },
    { "kind": "file", "details": { "path": "C:/Clutch-CS/content/result.json" } }
  ]
}
```

`items → ForEach → details → オブジェクトを展開` とつなぐと、メッセージの件名 `subject` と本文 `text` を個別に取り出せます。JSON Schema では、`kind` が `message` の場合だけ、この二つを必須の文字列とする条件を指定できます。ローカルの `$defs`／`$ref` と、`allOf` 内の `if`／`then`／`else` に含まれる項目名も出力候補に含まれます。

このように条件によって存在や型が変わる項目は、自動表示では「任意型・省略可能」となります。実行時には元の JSON Schema の条件全体を検証します。メッセージを処理する分岐で `subject` と `text` の型を確定したい場合は、展開ノードにも、この二つを必須の文字列とするスキーマを指定してください。JSON Schema のすべての条件を表示時に判定するわけではありません。

「制御」の **If-Start** (`control.ifStart`) は条件がTrueの場合、**IfNot-Start** (`control.ifNotStart`) はFalseの場合だけ「開始信号」(`start`)を送ります。比較ノードの真偽値を「条件」へ、開始信号を後続ノードの「開始」(`trigger`)へ接続します。処理用のデータは別の入力へ直接接続してください。

```text
ForEach.kind → 一致判定（右の値 = "message"） → If-Start.条件
If-Start.開始信号 → 後続処理.開始
ForEach.details → オブジェクトを展開
オブジェクトを展開.text → 後続処理.データ入力
```

開始信号を明示接続した後続ノードは、別のデータ入力が既に届いていても信号がなければ実行しません。不一致は`condition.notMatched`の意図した未実行で、ノードのエラーハイライトにはしません。文字列`"true"`・数値・nullなどの不正な条件は暗黙変換せず失敗し、後続へ開始信号を送りません。共通の「実行結果」は診断用です。分岐の制御には「開始信号」を使用してください。

逐次・並列ForEach内でも、非選択の処理をスキップして次の要素へ進みます。ForEach Endを使って値を集約する場合は各要素の結果値が必要なので、条件付き処理が実行されない要素にも値を供給する構成にしてください。Endの必須値が欠けたことを、成功した集約として扱う変更はしていません。
