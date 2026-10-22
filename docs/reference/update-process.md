# 更新の設定・予約・復帰の動作

[ドキュメント一覧](../README.md)

更新計画、日時予約、API・CLI、ファイル配置、結果確認の仕様です。通常の更新は[更新・修復・アンインストール](../guides/maintenance.md)を参照してください。

- [日時を指定して更新する](#日時を指定して更新する)
- [更新結果をフックへつなぐ](#更新結果をフックへつなぐ)
- [配置](#配置)
- [オフライン導入](#オフライン導入)
- [更新操作](#更新操作)
- [OS／RID が混在するクラスター](#osrid-が混在するクラスター)
- [検証範囲](#検証範囲)

## 日時を指定して更新する

Dashboardの **Settings & maintenance → Application updates** で、通常と同じリリース・配布元・対象・失敗方針を指定し、「日時を指定して更新を予約」を選択してください。日時は `2027-01-01T02:00:00+09:00` のように UTC オフセットを含めて指定してください。開始遅延の許容秒数は既定300秒です。保存は予約の確定であり、更新完了ではありません。

「更新予約一覧」から、起点ピアに保存された予約・更新結果を再取得し、未開始の予約を変更・取消できます。変更と開始前取消には表示した保存版を使います。開始意図が確定した後は取消を受け付けず、元更新IDで状態を確認します。

配布元ファイルを持つピアが予約を所有します。自動で他ピアへ担当を移しません。停止・保留中は指定猶予内で復帰を待ち、猶予超過は未実行にします。予約時の対象実体を固定するため、対象ピアが再起動して実体が変わった場合は予約を変更して再確認してください。開始したか不明な予約がある間は後続予約も自動開始しません。設定変更は既に開始した一回へ遡及しません。

CLIは `update schedules`、`update schedule-save --body reservation.json`、`update schedule-cancel --body cancellation.json`、`update schedule-status --id <更新ID>`、`update schedule-operation --id <予約変更ID>` です。

保存要求は `{ "operationId": "予約変更のGUID", "plan": { "startAt": "offset付き日時", "maximumDelaySeconds": 300, "update": { "operationId": "更新本体のGUID", "manifest": {}, "payloadRoot": "...", "peerIds": [], "failurePolicy": "rollbackSuccessful" } }, "expectedVersion": null }` の形です。

`update` には後述の完全な更新要求を入れ、変更では元更新IDを維持して `expectedVersion` に照会した版を指定します。取消は `{ "operationId": "取消のGUID", "updateOperationId": "元更新GUID", "expectedVersion": "照会した版" }` です。

## 更新結果をフックへつなぐ

通常の `control.hook` で `update.started`、`update.peerChanged`、`update.completed`、`update.failed`、`update.interrupted` を購読します。`value` に元の `operationId`、`outcome`、`completed`、対象別の `peerId`／`phase`／新旧実体IDが入ります。

全体更新では `clusterId` と `attemptOperationId`、ClientHost結果では `stage` も付与します。`update.completed` は失敗・不明を含む終端通知であり、成功分岐には `outcome` を確認してください。

通知は更新本体を起動する処理と分けます。発行試行を先に保存するため、クラッシュの隙間では通知が欠落する場合があります。応答不明を理由に同じ通知や更新本体を自動再実行しません。初回導入前の完了履歴は遡及再生しません。CoreServiceの `updates.events`、ClientHostの `/client/updates/events` で通知側の未確認を、元の更新結果とは別に確認できます。

## 配置

installation、staging、backup は絶対パスの独立したディレクトリを指定します。常駐サービスの設定、永続データベース、ログは installation の外に置きます。Updater 一式も installation の外に配置します。Windows で実行中の helper 自身を入替対象にしません。許可ルート内でも symlink / reparse point を通るパスは拒否します。

```toml
[updates]
installationRoot = "D:/Clutch-CS/service"
stagingRoot = "D:/Clutch-CS/staging"
backupRoot = "D:/Clutch-CS/backups"
updaterExecutablePath = "D:/Clutch-CS/updater/ReArtWord.Contents.BackgroundService.Updater.exe"
serviceKind = "directProcess"
interfaceId = "実際の NIC ID"
stopTimeoutMilliseconds = 30000
startTimeoutMilliseconds = 120000
forceStopOnTimeout = false
rollbackFailurePolicy = "excludeFailed"
```

クラスター管理・稼働確認・正常終了の確認は、既存 UDP オブザーバーと明示 NIC／local address／ブロードキャスト／共通 UDP ポートを使います。CoreService に HTTP 待受はありません。OS サービスとして導入した場合は `windowsService` / `systemd` / `launchd` と登録済み `serviceName` を指定し、サービス manager に停止と起動を要求します。権限不足・停止未確認を成功扱いしません。

起動確認にはプロセス開始だけでなく、ピアの再発見と双方向の同期も含まれます。既定値は120秒です。既存設定で明示した値は維持され、開始済みのジョブには設定変更が遡及しません。

旧版復元後も確認期限に達した `update.rollbackHealthUnconfirmed` は、自動で成功や復帰済みへ変換しません。旧版の Ready と版・実体を別途確認できた場合は、installation 外の Updater に `--confirm-restoration <確認JSONの絶対パス>` を渡せます。

JSON は `jobPath`（元の job.json）、`releaseId`、`buildVersion`、`instanceId` を指定します。この処理は元ジョブの指紋・互換旧版の全ファイル・指定実体の実際の稼働状態を照合し、停止や入替を行わず元操作の復帰結果だけを確定します。その後 Dashboard の元操作を再確認して保留を解除し、新しい更新計画を作成します。

Updater の終了コード1は更新失敗と旧版復帰が確定した意味であり、返却JSONの理由も確認してください。

## オフライン導入

1. 対象 OS/RID の発行成果物をペイロードに集めます。`installer/New-ReleaseManifest.ps1` へ絶対ペイロード root、リリース ID、RID、apphost entryPoint を渡し、全ファイルの size / SHA256 を記録します。互換リリース ID は実際に互換性を確認した値だけを宣言します。
2. `installer/Install-Offline.ps1` にペイロード、未作成 installation、外部 configuration パス、サービス artifact root、対象 OS を渡します。ネットワーク取得はせず、全検証後に初期配置します。既存 installation は拒否します。
3. 配置・登録・通常参加確認まで続ける場合は `-RegisterAndStart -VerifierExecutablePath <installation外のUpdater絶対path>` を指定します。事前に TOML を用意し、マニフェストの `expectedBuildVersion` を実 CoreService 情報版へ合わせます。管理者/root 権限で、実 RID/NIC/config/checksum 確認、OS サービス登録・開始、実 UDP のピア/instance/PID/実行 path/RID/buildVersion/Ready 確認まで行います。詳細は [installer の説明](../../installer/README.md) を参照してください。
4. `-RegisterAndStart` を省略した既定モードは、配置と登録用 PowerShell／systemd unit／launchd plist の生成までです。この場合は管理者が登録・起動して通常参加を別途確認します。systemd unit は `/etc/systemd/system`、launchd plist は `/Library/LaunchDaemons/<serviceName>.plist` が更新接続実装の前提です。初回リリースにも `release-manifest.json` を残します。

## 更新操作

### セットアップからWebクライアントを更新する

セットアップではCoreServiceとWebClientを別々に選択できます。このPCでは独立Updaterを直接起動し、接続先では関連するCoreServiceの `setup.client.*` 管理操作と、一回限りのTCP転送を使用します。WebClientだけの更新ではCoreServiceを停止しません。

接続先のWebClientはCoreServiceの導入記録から特定し、導入ID、稼働中プロセスのPID・起動時刻・実行パスと版を照合します。WindowsサービスのCoreServiceは稼働中WebClientのユーザートークンを引き継いでUpdaterを起動します。ユーザー権限のCoreServiceは、同じユーザー・セッションで動作するWebClientを更新できます。異なるユーザーや管理者権限の対話Coreからの更新は受け付けません。

管理対象は自動起動登録済みで、プログラムが `app` などの独立したサブフォルダーにある配置です。全ファイルの検証後にプログラムだけを入れ替え、設定とユーザープロファイルは保持します。実行ファイル名が旧名でも、現在の起動名へ導入記録と起動登録を更新します。起動確認に失敗した場合は元のファイルと登録へ戻し、復帰の確認に失敗した場合は結果不明とします。

操作入力と結果はWebClientの配置先にある `update-staging/operations/<操作ID>` に保存します。元要求の受付後は自動再実行せず、保存結果だけを照会します。接続先の結果を確認できない場合は、そのPCの `result.json` と稼働状態を確認してください。

### 画面から全クラスターをホスト単位で更新する

Dashboard の **Settings & maintenance → Update all discovered peers** は、接続先ピアを選択せずに使用できます。

1. `New-InstallPackage.ps1` が生成した `fleet-release.json` を選び、配布元にパッケージのルート（`core` と `client` の親）を指定します。CoreServiceだけを更新する場合は従来の `release-manifest.json` または `release-bundle.json` も利用できます。配布元の絶対パスは、画面を配信している **ClientHostのPC上** のディレクトリです。各CoreServiceのstagingへ事前コピーする必要はありません。
2. 必要な場合だけ稼働中更新を許可し、最大試行回数（既定3回、初回を含む）、最大並列数（1〜3、既定2）、ピアごとの確認期限を設定します。
3. **Preview update plan** で、発見中の全クラスター・全ピア（最大256件）の固定対象、ホスト順の優先順序、並列上限、一台構成のクラスターを確認し、**Start this update** で開始します。開始時にも実体・ホスト・NICと配布元ファイルのSHA-256を検証します。
4. 異なるPC・クラスターの対象を空き枠へ割り当てます。同一PCまたは同一クラスターのピアは同時更新せず、新しい実体、指定版、Ready、maintenance解除まで枠を保持します。発見一覧から消えた停止中ピアもCoreServiceの所属情報で数えます。3ピア以上のクラスターでは、停止する対象を除いて `floor(ピア数 / 2) + 1` 以上のReadyを、準備前と停止直前に確認します。例えば3ピアでは残り2ピア、4ピアでは残り3ピアのReadyが必要です。2ピアで複数PCのクラスターは相手のReadyを確認し、2ピア以下で単一PCのクラスターは一時切断を許容します。条件を満たさない対象は保留し、安全に開始できる別対象を先に処理します。

並列数は固定計画へ保存され、開始後の編集は実行中の更新へ遡及しません。進捗保存を直列化し、結果不明が出た場合は新規更新を止め、開始済みの他ピアは結果確認まで回収します。未着手ピアの更新を自動再開しません。

既存UpdaterによるCoreService一式の入替を使用します。`fleet-release.json` にClientHostが含まれる場合は、全CoreServiceの成功後に、この操作を実行しているClientHostも最後に更新します。CLIおよび別PC上のClientHostは対象外です。稼働中の版との互換性がマニフェストに宣言されていることが必要です。更新済みの互換版は維持し、失敗したピアでは既存Updaterの旧版復元結果を確認します。

通常処理の保留は更新するピアだけで行い、全クラスターをまとめて保留しません。

停止開始前の配布失敗、明示的な未実行、または旧版への復帰とReadyを確認できた失敗だけを最大回数まで再試行します。各試行の操作IDは保存します。停止・入替・復帰が結果不明の場合は自動再試行も後続ホストの更新も停止します。**Recheck saved updater results** は元のUpdaterの結果を再照会し、確認できた入替・復元のmaintenanceだけを解除します。新たな入替要求は送りません。

更新と進捗はClientHostが所有し、ブラウザーの画面移動・再読み込み・接続先切替後も続行します。同じClientHostを開いた画面で進捗を確認できます。ClientHost自体が停止した場合は、再起動時に元IDを保持したまま未確認として復元し、更新を暗黙に再開しません。元結果を再確認してから、未着手・保留の対象を新しい計画で更新してください。

画面には直近20操作を表示し、元IDの重複受付を防ぐ記録は `--state` 配下の `fleet-updates` に保存します。

### ClientHostと自動起動の維持

`New-InstallPackage.ps1 -CompatibleReleaseIds <互換性を確認した旧releaseId>` でCoreServiceとClientHostを含む配布を作ります。ClientHostを含む計画では、開始前にClientHostの候補・全ハッシュ・独立Updaterを準備します。ClientHostとその親ディレクトリへの書込み権限が必要です。`--state` はインストール先の外に置きます。

旧版にこの機能がない初回だけは、通常の導入手順で対応ClientHostへ更新してください。

全CoreServiceが成功した後、元と同じユーザー権限のUpdaterへ引き渡し、ClientHostを正常終了します。既存のインストール先を維持したまま新版へ入れ替えるため、Windowsのログオンタスクは登録済みのパスから新版を起動します。通常更新でタスクの管理者権限や再登録を要求しません。待受URL、保存先、利用者も維持します。別の配布ディレクトリへ手動で起動先を変更した場合は、自動起動登録の変更が必要です。

画面は一時的に切断します。再接続・再読み込み後も同じ操作IDでCoreServiceとClientHostの結果を確認できます。新版の実PID・起動時刻・assembly版とHTTP応答を照合してから成功とします。新版の起動に失敗した場合は、起動した新版の停止を確認して旧版を復元・再起動します。旧プロセスが終了しない場合は入替を行いません。結果不明の操作を自動で再実行しません。

`--state/client-updates/<操作ID>` に固定job、helperと結果を保存し、インストール先の隣に `<元ディレクトリ名>.backup-<操作ID>` を保持します。失敗した候補は診断用に残します。成功と稼働を確認した後、不要な旧バックアップ・候補・helperを管理者が整理できます。履歴だけを消して同じ操作IDを再利用しないでください。

自動再起動では接続中のデスクトップとTerminalが切断されますが、CoreService所有の共有ファイル転送キューは継続します。

### 更新ファイルの世代整理

更新用ファイルは、起動30秒後と次回CoreService更新前に世代整理します。ClientHostは起動後に整理し、Updaterへの短い引き渡し待ちには含めません。同じ配置について直近3件の成功済み更新と24時間以内の成功済み更新を保持し、それより古い成功分の旧実行物・転送ペイロード・Updater helperを回収します。実行中、失敗、中断、結果不明、別配置、検証不能な記録は対象外です。操作記録・設定・業務ファイル・ワークフローのスナップショットは残ります。

保持期間外の版へ戻す場合は、その版の配布パッケージを用意してください。手動で置いた配布パッケージや診断資料は自動削除しません。

### TCP配布がUDPへフォールバックする場合

更新用TCPポートはCoreService起動時に動的に割り当てられます。TCP待受が存在しても受信が許可されていないPCでは、従来のUDP転送で配布を継続します。大きな配布物では、この差で所要時間が大きく変わります。

Windowsでは、管理者が実際のCoreService導入先、クラスター側のIP、ClientHostの送信元IPを確認し、その組み合わせに限定してTCP受信を許可できます。以下のパスとIPは例です。複数CoreServiceを同じPCで動かす場合は、実行パスとクラスター側IPごとに固有のルール名を使用します。インストーラーが自動でFirewall設定を変更することはありません。

```powershell
New-NetFirewallRule -Name 'Contents-Update-TCP' -DisplayName 'Clutch-CS update TCP from ClientHost' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP -Program 'C:\Clutch-CS\service\Clutch-CS.CoreService.exe' -LocalAddress '192.168.1.20' -RemoteAddress '192.168.1.10'
```

画面は未完了の操作を「実行中」と完了ピア数で表示し、終了後の結果不明と区別します。試行の詳細には、準備・入替・確認などの失敗段階と診断コードが残ります。例外本文や要求内容は診断コードへ含めません。後の再試行が成功しても以前の試行IDと診断は保持されるため、問題が起きた段階を元IDで確認できます。

### クラスター単位の更新

UDP管理操作 `updates.start` に次のJSONを送ります。`manifest` には配布物の `release-manifest.json` 全体、`payloadRoot` には要求元ピアの一時配置先にある検証対象を指定します。管理要求にはクラスターID・ピア ID・元の実体ID・要求ID・受付期限を固定します。

```json
{
  "operationId": "5d30a72c-5f80-49db-993c-b545111beed1",
  "manifest": {
    "releaseId": "release-2",
    "platform": "win-x64",
    "entryPoint": "Clutch-CS.CoreService.exe",
    "files": [],
    "compatibleReleaseIds": ["release-1"]
  },
  "payloadRoot": "D:/Clutch-CS/staging/release-2",
  "peerIds": ["peer-a", "peer-b"],
  "failurePolicy": "rollbackSuccessful",
  "rollbackFailurePolicy": "excludeFailed",
  "allowWhileActive": false,
  "remainingOperationConfirmed": false,
  "peerTimeoutMilliseconds": 600000
}
```

上の `files: []` は説明用です。実行時は生成済みマニフェストを入れてください。対象ピア集合は操作開始時に固定します。全対象への転送とハッシュ検証を終え、固定対象全ての通常受付保留を確認してから入替を開始します。全体進行も独立 Updater で動くため、要求元 CoreService が更新で停止しても同じ操作を進行できます。

対応するCoreServiceとUpdaterは、UDP管理経路で転送能力を照会し、選択NIC上のTCPでペイロードを連続送信します。1 MiBの作業バッファーを再利用し、ファイル単位でSHA-256検証と永続flushを行います。TCP ポートはCoreService起動時に割り当てられ、能力照会で通知されます。接続前に同一NICからのTCP到達性を確認し、旧版またはTCP未到達の場合は従来の32 KiB UDP転送へ戻ります。

高速経路を使う環境では、既存ネットワーク方針に従い、そのCoreService実行物への対象LAN内TCP受信を許可してください。インストーラーはFWを変更しません。一回のトークンを発行した後の切断や応答消失は、同じ転送結果の照会へ移り、データを自動再送しません。coordinator自身のペイロードは、固定local ジョブを検証してOSコピーで配置します。

`New-InstallPackage.ps1` はCoreServiceとClientHostに同じ版のUpdater マニフェストと差分の依存ファイルを同梱します。同一内容のruntimeは一重に格納し、版の異なる依存ファイルは分離します。更新開始時に、検証したUpdater一式をその操作のstaging内へ組み立ててから起動するため、実行中helperはサービス入替の影響を受けません。

同梱版がない従来packageでは、これまでの `updaterExecutablePath` を使用します。

UDP 参照操作 `updates.read` に操作 ID の JSON string を渡し、最終結果を確認します。同じ操作 ID の再要求は保存結果を返し、副作用をもう一度行いません。helper 起動意図だけが残った場合や、途中で helper を失った場合は Unknown / Interrupted として人が実状態を確認します。再試行が必要なら現状態を確認し、新しい操作 ID を明示します。

## OS／RID が混在するクラスター

一回の要求へ任意の `variants` を追加できます。各 variant は `manifest` と bundle 内の `relativePayloadPath` を持ちます。全マニフェストの `releaseId` と `compatibleReleaseIds` は共通とし、`platform` は RID ごとに一意にします。従来の単一マニフェスト入力はそのまま利用できます。

```json
{
  "payloadRoot": "D:/Clutch-CS/staging/release-2-bundle",
  "variants": [
    { "manifest": "ここを win-x64 の完全な manifest object に置換", "relativePayloadPath": "win-x64" },
    { "manifest": "ここを linux-arm64 の完全な manifest object に置換", "relativePayloadPath": "linux-arm64" },
    { "manifest": "ここを osx-arm64 の完全な manifest object に置換", "relativePayloadPath": "osx-arm64" }
  ]
}
```

上は既存要求への追加箇所を示す説明用断片です。

`installer/New-ReleaseManifest.ps1` で各ディレクトリのマニフェストを作り、`New-ReleaseBundle.ps1 -BundleRoot <絶対root> -RelativePayloadPaths win-x64,linux-arm64,osx-arm64` で生成した `release-bundle.json` の `manifest` と `variants` を要求へ入れてください。

初期導入は対象 RID の個別ディレクトリを既存 `Install-Offline.ps1` へ渡します。

Updater は固定対象全てへ実 RID を照会し、それぞれの成果物を選択して全チェックサムを確認します。この事前確認が一つでも不足・破損・不一致なら、転送・受付保留・停止をまだ行わず `NotExecuted` で終了します。近い OS／CPU の成果物への暗黙 fallback は行いません。選択後にピアの RID が変わった場合も、受信側の準備検証で拒否します。リリース ID、互換版集合、失敗・復元方針はクラスター全体で共通のままです。

`keepCompatibleMixed` は確認できた互換版の混在を維持します。`rollbackSuccessful` は更新成功を確認したピアのみ旧版へ復元します。復元不能は既定で当該ピアを除外し、`stopCluster` を事前指定した場合は固定対象の通常実行を永続保留します。通常実行の保留は同じ操作 ID だけが解除できます。開始済み処理は drain し、新規処理を受け付けません。動作中更新は既定拒否で、明示許可と残存動作確認、互換性宣言が必要です。

## 検証範囲

画面用APIの全体更新は `FleetUpdateProcessTests` で、隔離した二クラスター・二ホスト・四つの実Windows CoreService、実ClientHost、実Updaterを使い、配布・最大2件の実並列更新とPC/クラスター排他・新実体のReady・保留解除・ClientHost再起動後の結果復元を確認します。残存ホストの喪失、単一ホストの一時切断許可、最大3試行、確認済み復元後の再試行、結果不明時の後続停止と非再実行はWeb クライアント Testsで確認します。

異 RID の選択、必要 RID 不足、選択内容の破損による全転送前拒否、リリース／互換方針不一致は模擬ピアの試験で検証します。実他 OS の起動はこの環境では未確認です。`UdpCoreUpdateTests` の bundle ケースは RID 別 bundle 入力でも二つの実 Windows CoreService の全入替を確認します。

Unit Tests の Updates は実子プロセスと UDP 稼働確認による正常更新・壊れたペイロード・旧版実再起動・途中状態非再実行・単独実行実行を検証します。`UdpCoreUpdateTests` は二つの実 CoreService の UDP 発見、独立 Updater からの全ファイル転送、要求元を含む両サービスの実停止・入替・新実体起動、maintenance 解除を確認します。

`Test-Installer.ps1` は三 OS 用成果物の生成、内容チェックサム、PowerShell / XML 構文、破損ペイロードの導入前拒否を確認します。

```powershell
dotnet test tests/ReArtWord.Contents.BackgroundService.Unit.Tests/ReArtWord.Contents.BackgroundService.Unit.Tests.csproj --no-restore --filter FullyQualifiedName~Updates --logger 'console;verbosity=normal'
```

管理書込みは一度だけ送信し、応答を失った場合は同じ元 command の操作記録を照会します。別 Request ID や再起動後の別実体に副作用を再送しません。自身の正常停止も、固定ジョブの指紋と元操作記録の Completed を CoreService が確認してから実行します。helper は OS の実 PID 終了と、新実体の実稼働確認を別に確認します。

Updaterは、照会応答自体の欠測や受付記録がまだ見えない場合も、元commandの期限内では読み取り照会を続けます。照会不能を元処理の未実行・停止と判断しません。確認できないまま期限へ達した場合はUnknownを維持します。これは、ホスト単位更新で確認済み未実行・旧版復帰後に行う新しい試行とは別です。

実 Windows サービス登録と Linux / macOS サービス manager 操作は、この Windows 試験用の構成環境では未検証です。UDP の分割・ハッシュ・TTL は既存 ManagementTransport の唯一の実装を利用します。
