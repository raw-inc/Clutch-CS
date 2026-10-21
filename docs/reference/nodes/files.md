# ファイルノード

[ノード一覧](README.md) · [サンプル](../../../samples/generic-files/README.md)

ファイルノードには絶対パスを指定し、そのファイルがある PC を実行先に指定してください。PC 選択ノードまたはピアの直接指定を使えます。実行先の指定がない場合は、最優先の PC で実行します。

操作できる範囲は、実行先の CoreService が持つ OS のアクセス権で決まります。退避先のディレクトリはピアごとに設定します。競合時の `ConflictPolicy` と失敗時の `InternalFailurePolicy` は、処理の開始時にクラスターの共通設定から取得し、その処理が終わるまで使います。

画面では、実行PCを指定してからパス欄の **参照…** を押すと、そのピアのOSアクセス権で読めるドライブ・フォルダーを閲覧できます。転送元パスは「転送元ピア」、転送先パスは実行先ピアの一覧から参照できます。手入力も使用でき、一覧で親フォルダーを開いて新しい名前を指定することもできます。この選択操作ではファイルを作成・変更しません。複数PCや実行時に決まる対象は、一台を確認できるまで一覧で断定しません。

パスやピアを複数ノードで使う場合は **ファイルを選択**（`value.file`）、**フォルダーを選択**（`value.directory`）、**ピアを選択**（`value.peer`）を追加し、各設定欄へ接続します。ピア選択はピアID、パス選択は絶対パスの文字列を返します。パスを接続するだけでは実行先は変わらないため、接続先のPC指定も合わせてください。パスノードのピアを省略した場合、接続先の対象が一台に決まればそのピアの一覧を使います。

```csharp
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

FileSystemOptions options = new(
    AllowedRoots: [],
    ConflictPolicy: EFileConflictPolicy.Fail,
    InternalFailurePolicy: EFileInternalFailurePolicy.Keep,
    RecoveryDirectory: "C:/Clutch-CS/data/file-recovery",
    UseOperatingSystemAccess: true);
FileTransferService files = new(options);
IReadOnlyList<INode> nodes = FileSystemNodeRegistration.Create(options, peerFileTransfer, files);
```

`RecoveryDirectory` は操作するコンテンツ tree の外に置きます。未指定時は利用者の LocalApplicationData 内の `ReArtWord.Contents.BackgroundService/file-recovery` です。退避には変更対象のコピーが必要になるため、保存領域の空き容量も必要です。

相対パス、symlink・junction・reparsepoint を通るパスは拒否します。変更操作ではファイルシステムのルート自体を指定できません。コピー元・コピー先の双方を検証し、同一または包含関係のある source と destination は拒否します。同一ホストの別ピアも正規化したパスの重複範囲を共有予約し、最終操作前にもパスと祖先を再確認します。内部の更新stagingなどに使う限定領域の検証は別途維持します。

外部プロセスが検証直後にディレクトリを置き換える敵対的競合を OS の相対ハンドル操作で封じる実装は対象に含めていません。

| 種類 | 存在前提と出力 |
| --- | --- |
| `file.read` | 既存ファイル。`value` にバイト列。`maxBytes` 既定 1 MiB、最大 16 MiB |
| `file.create` | `value` を作成。既存先の扱いはファイル存在時モードに従う |
| `file.replace` | 既存ファイル。内容を置換 |
| `file.upsert` | 不存在なら作成、既存なら置換 |
| `file.append` | 既存ファイルの末尾へ追記。不在時には作成しない |
| `file.copy` / `file.move` | 既存ファイルと宛先絶対 `destination` |
| `file.rename` | 同じ親内の絶対 `destination`。親を変える場合は move |
| `file.delete` | 既存ファイル。`missingOk: true` の場合だけ不存在を正常終了 |
| `file.exists` / `directory.exists` | 種類が一致する実体の有無を `value` に出力 |
| `directory.create` | 必要な親も再帰作成。同名ディレクトリは既定で正常終了 |
| `directory.list` | 既存ディレクトリ。`value` はパス・種類・size の配列 |
| `directory.copy` | 既存ディレクトリ。空でない tree には `recursive: true` が必要 |
| `directory.move` / `directory.rename` | 既存ディレクトリ。rename は同じ親内 |
| `directory.delete` | 既定は空ディレクトリのみ。tree 削除は `recursive: true` を明示 |

作成・書込み・コピー・移動・名前変更・ピア間転送、およびメディア変換では、保存先の親ディレクトリを必要に応じて再帰作成します。`existingFileMode` は `overwrite`（既定）／`skip`／`error`、`lockedFileMode` は `error`（既定）／`skip` です。スキップは正常終了で、`value` に既存先の `peerId`・`path`・`skipped: true`・`skipReason` を返します。

既存先の内容やハッシュを新規作成したものとして扱いません。ロックを強制解除せず、アクセス権不足・容量不足などはスキップへ読み替えません。

旧設定で明示した `overwrite: false` は上書き禁止（`error`）のまま維持します。`existingFileMode` を明示した場合はそちらが優先です。ファイルとディレクトリの種類違いは上書きでも拒否します。上書き先が空でないディレクトリの場合は `recursive: true` も必要です。ディレクトリの上書きはツリーの置換で、マージではありません。`directory.create` は既存ディレクトリの内容を消しません。

copy/move の宛先は、ファイルを置く親ではなく、作成する最終パスを指定します。

書込み入力は `value` です。Binary はそのまま、JSON string は UTF-8、それ以外の JSON は JSON 表記で保存します。`file.replace` と `file.append` は既存ファイルを必要とし、append の上書きモードは追記を継続する意味です。list の `recursive` は既定 false、`maxEntries` は既定 10000、最大 100000 です。

競合方針 `Fail` は変更前に失敗し、`Wait` は既存の処理を待ちます。待機後に存在・許可範囲・上書き前提を再確認します。read 同士、別ファイル、別サブツリーは並行できます。上位 tree の変更はその配下の read/write と競合します。

内部途中失敗の `Keep` は途中状態を保持し、`Restore` は一台内の実行前退避へ戻します。これは各ノード設定の `failurePolicy: "keep" | "rollbackAll"` と別です。後者は root runtime が全実行対象を事前退避し、複数ピアの結果失敗時に補償するための設定です。成功した副作用を後から取消しただけで自動 undo は行いません。

補償の public ポートは `ICompensableFileNode.CaptureAsync(context)`、`RestoreAsync(token)`、`ReleaseAsync(token)` です。Capture と Execute は同じ ExecutionId を使用し、Release まで対象リースを保持します。退避内容とトークンのマニフェストを保存し、復元前に全内容の SHA256 を照合します。破損・欠損は対象を変更する前に拒否します。

再作成したサービスでも同じ保存先とトークンで明示復元できます。ACL・所有者・時刻など OS メタデータの復元を保証する filesystem image スナップショットではなく、実体の内容・tree・元の不存在を補償します。

スキップが確定した回は補償でも既存先を変更しません。退避準備の後に保存先が新しくできた場合も、スキップの記録を確定してから成功を返します。その記録を確認できない場合は結果不明として停止し、外部で作成された内容を自動復元で消しません。

## ファイル内容とテキストの変換

`file.read`の`value`はバイト列です。文字列として使う場合は、`file.read` → **バイト列をテキストに変換**（`text.decode`）→ 後続ノードの順に接続します。逆方向は、文字列 → **テキストをバイト列に変換**（`text.encode`）→ `file.create`／`file.replace`／`file.upsert`です。どちらもファイルパスを開く処理は持たず、入力値を変換します。

文字コードはUTF-8、UTF-16 LE/BE、UTF-32 LE/BE、Shift_JIS（Windows-31J／CP932）、EUC-JP、ISO-2022-JP、ASCII、ISO-8859-1、Windows-1252 から指定できます。復号の既定は自動で、先頭にUnicode BOMがあればその形式、なければUTF-8です。BOMなしのShift_JISなどを曖昧な推測で選ばないため、該当形式を明示してください。符号化の既定と「自動」はUTF-8です。

| BOMの扱い | バイト列→テキスト | テキスト→バイト列 |
| --- | --- | --- |
| 自動（既定） | 一致する先頭BOMがあれば除去 | UTF-16/32に付与。UTF-8・従来の文字コードには付与しない |
| あり | 一致するBOMを必須として除去 | BOMを付与。BOMのない文字コードでは失敗 |
| なし | 全バイトを本文として読み、先頭U+FEFFも保持 | BOMを付与しない |

復号の自動判別はUTF-32の長いBOMをUTF-16より先に確認します。文字コードを明示した場合はそのBOMを優先するため、UTF-16LEのBOM直後にNULがある本文も正しく保持します。明示文字コードと異なるBOM、不正なバイト列、符号化で表現できない文字は失敗し、代替文字で成功扱いにしません。改行、空白、本文中のU+FEFFは変更しません。既存の`data.text.parse`やJSON等の解析ノードの挙動も引き続き適用されます。

## 大きいファイルとホスト間の転送

`file.transfer` はノードを実行する PC が宛先です。転送元は `sourceHost`（ホスト ID）と `sourcePath`、宛先は `path` に指定します。転送先PCは「PC選択」ノードを「実行PC」入力へ接続して指定してください。クラスター選択はありません。転送元・転送先ともワークフロー所属クラスター内に限定します。

転送元ホストに複数ピアがある場合、共通PC選択と同じ参加状態・優先度・連続参加開始時刻・ピア IDの順で一つを選び、予約・退避・転送へ同じピアを渡します。選ばれたピアのOSアクセス権を使い、権限不足や転送失敗では別ピアへ切り替えません。既存の `sourcePeer` 設定も利用できますが、`sourceHost` とは併用できません。画面で転送元ホストを選ぶと、接続されていない旧ピア設定を置き換えます。

通常の転送では `requestId` は省略できます。実行ごとに新しいIDを発行するため、同じワークフローを次回実行して新しい内容を転送できます。特定の途中転送を明示的に再開する場合だけ、元と同じIDを設定します。

発行したIDは成功・失敗結果の `requestId` 出力（転送ID）から確認できます。結果不明の場合は元の要求を照会し、実行状況を確認してから再開を判断してください。

```json
{"sourceHost":"source-pc","sourcePath":"C:/Clutch-CS/assets","path":"C:/Clutch-CS/received","requestId":"asset-delivery-001","recursive":true,"overwrite":false,"failurePolicy":"rollbackAll"}
```

ファイル本文を巨大な NodeValue 一個にしません。source の size/SHA256 と tree を確認し、最大 32 KiB の RPC 分割データを読み込み、宛先へ staging します。一覧は最大 16 件のページです。各分割データと全ファイルの内容、全マニフェストの一致、および転送完了前の source 再確認を経て宛先へ確定します。ディレクトリは空ディレクトリも含めて転送し、`recursive: true` を必須にしています。

途中失敗や取消では自動再試行せず、宛先親の `.contents-transfer-*` に確認用の途中状態を残します。同じ `requestId`、source、destination、内容で利用者が明示再要求した場合、確認できた既存分割データを再利用します。既存分割データや内容指定が異なる場合は拒否します。新しい内容を転送する場合は新しい requestId を使用してください。完成済みの明示再要求も宛先内容を再検証します。

不要段階は `DiscardStageAsync` で明示破棄します。

`IPeerFileTransfer` は `StatAsync/ListAsync/ReadChunkAsync/OpenStageAsync/StageDirectoryAsync/WriteChunkAsync/CommitStageAsync/DiscardStageAsync` の有限 RPC ポートです。`FileTransferService` は同じ API から peerId を除いた handler サービスを提供します。

ホストは通常のピア UDP 通信へ接続し、ログ転送の TCP には載せません。`FileStageRequest.ExecutionId` は補償で取得済みの宛先リースを再利用するための関連付けです。

この節はワークフローノードの経路です。[GUIのFileTransfer](../file-transfer.md)は、ホスト間キューとブラウザー中継で対応CoreServiceへの1MiB TCP転送を利用し、未対応または接続開始前の未到達時に32KiBの管理経路へ戻ります。ノードの32KiB RPC経路とGUIの高速転送経路を区別してください。どちらも共通の段階保存と内容検証を使います。

## 変更監視

`file.watch` は `path`、`recursive`、`filter`、`count` を設定します。`filter` は既定 `*`。OS 通知の受付時に設定を固定し、`value` に `{path,oldPath,change}` を出力します。OS 通知の重複を暗黙削除しないため、必要な場合は後続の汎用 dedupe を組み合わせます。

root 喪失中は Unavailable にし、同じ許可パスの復帰後に再 listen します。不正な一通知は一件 failure、OS バッファーまたは内部 1024 件キューの overflow は欠落を明示する failure とし、listener 全体の成功へ隠しません。既定は無期限、count 指定または予定停止で新受付を止め、取得済み通知を drain します。

Windows tempdir の操作・実 junction・FileSystemWatcher による root 復帰、補償マニフェストの再読込み・破損、転送段階の再読込み・分割データ不一致・tree 不一致を試験しています。Windows上の別CoreServiceプロセスとClientHostを使い、ピア別の一覧取得、リンク・不正なパスの拒否、選択ノードを接続した実UDP転送、次回実行による内容更新も検証しています。

別OSのsymlinkやOS固有のファイル権限は、その環境で確認してください。
