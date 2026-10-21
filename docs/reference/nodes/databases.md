# データベースノード

[ノード一覧](README.md)

ワークフローから SQLite・PostgreSQL・Redis を操作するノードです。ノードを実行するピアと接続先を指定してください。Clutch-CS 自身の設定・状態の保存先とは独立しています。書き込みや commit の途中で接続が切れた場合は、結果を確認してから再実行するか判断してください。

## SQLite・PostgreSQL

`database.sqlite` は実行ピア上の `path` を指定します。既存ファイルを既定とし、作成する場合だけ `createIfMissing: true` を指定します。ファイルへの到達範囲は実行ピアのファイル設定・OS権限に従います。

`database.postgresql` は、PostgreSQL接続文字列を登録した `secretId` を指定します。接続秘密値の例は `Host=db.example;Database=contents;Username=contents_app;Password=...;SSL Mode=VerifyFull` です。接続・証明書・ホスト選択はNpgsqlの設定と実行ピアのOSルーティングに従います。NICの独自bind設定は提供しません。

接続プールとambient transactionへの自動参加は無効です。

両ノードで `sql` と `mode` を指定します。`query` は一つの結果集合を行オブジェクトの配列として `value` に返し、`execute` は変更件数のオブジェクトを返します。`affected` は変更件数で、プロバイダーから得られない場合は-1です。重複列名にはSQLのaliasを付けてください。複数の結果集合は受け付けません。

```sql
INSERT INTO measurements (device_id, value) VALUES (@device, @value)
```

入力端子 `parameters` へ `{"device":"sensor-a","value":42}` を渡します。値をSQL文字列に連結する必要はありません。通常はJSONの区分から型を決め、必要な場合は設定 `parameterTypes` で `string`、`integer`、`number`、`decimal`、`boolean`、`binary`、`datetime`、`guid`、`json` を明示します。

binary入力はbase64、日時はoffset付き文字列です。jsonパラメーターはJSON本文の文字列として渡すため、PostgreSQLのjsonb列等にはSQL側で `CAST(@document AS jsonb)` と指定します。nullの型が必要な場合も明示してください。

既定の取得上限は10,000行・16MiB、期限は30秒です。上限を超えた部分結果は成功として返しません。出力を確認してから明示トランザクションをcommitします。失敗時はrollbackを試みますが、SQL自身のトランザクション制御や外部関数の副作用まで取消済みとは保証しません。SQLの明示拒否は `failure`、開始前の入力不正・接続不能は `notExecuted`、送信後の通信断・取消・未確認commit等は `unknown` へ進みます。

SQLiteの計算はVMの進捗コールバックで期限を確認し、lock待ちは秒精度のプロバイダーtimeoutを使用します。

文字列・真偽値・数値・日時・GUID・binaryをJSONへ返します。binary列はbase64、日時・GUIDは文字列です。プロバイダー固有の配列・複合型等は、必要なスカラー／JSON本文へSQLで変換してください。

## Redis

`database.redis` に実行先の `interfaceId`、`localAddress`、`remoteAddress`、`remotePort` を設定します。操作は `get`、`set`、`delete`、`exists` です。`key` 入力へ文字列を、set時は `value` へ保存値を渡します。値形式 `format` は `text`、`json`、`binary` から指定できます。

パスワードは `secretId` で参照し、ACLを使う場合は `username` も設定します。TLSは `tls: true` と証明書の `serverName` を指定し、OSの証明書検証を行います。`database` はDB番号、`ttlMs` はsetの有効期間（0は期限なし）です。任意コマンド、Redis クラスターのMOVED再接続、トランザクション、自動再試行は提供しません。

getは `exists` と、存在する場合だけ `value` を返します。欠落キーと空値を区別してください。deleteは `affected`、existsは `exists` を返します。認証・DB選択の拒否は業務操作の `notExecuted`、キー操作の明示拒否は `failure`、送信後に応答を確認できない場合は `unknown` です。既定は30秒、要求・応答上限は1MiBです。
