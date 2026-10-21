# 設定ファイルとスクリプトで導入する

[ドキュメント一覧](../README.md) · [GUI でインストールする](install.md)

以下は **Windows x64の新規PCを2台使う手順**です。最初の PC でクラスターを作成し、その PC を起動したまま 2 台目を参加させてください。クラスターに参加する CoreService の単位を「ピア」と呼びます。

CoreService の起動設定で、参加するクラスターを指定してください。ClientHost の **Select connection** は、管理画面の接続先を選ぶ操作です。管理画面を使うだけの PC には ClientHost を配置してください。

## 1. 配布物とLANを用意する

- 配布担当から、対象PC用の**自己完結型の配布一式**を用意してください。まだない場合は、[配布物を作成する](../development/distribution.md)をビルドPCで実行してください。導入先に.NET SDK／.NET Runtimeを別途インストールする必要はありません。
- 導入先には **PowerShell 7**を[Microsoftの導入手順](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows)から用意し、インストール時は管理者として起動してください。Windows 標準の PowerShell 5.1 には対応していません。導入スクリプトは、登録前に対象の OS・CPU アーキテクチャ（RID）を検証します。
- 各PCに、参加先LANの安定した IPv4 アドレスを割り当ててください。PC同士でUDPブロードキャストが届くネットワークを使用してください。異なるサブネット／VLANをルーター越しに指定するだけでは発見できません。

この手順では次の値を使います。IP・NIC・ネットワーク範囲は実環境へ置き換えてください。

| 項目 | PC A：最初の1台 | PC B：参加する2台目 |
| --- | --- | --- |
| クラスターID | `clutch-cluster` | `clutch-cluster`（同じ値） |
| ピアID | `peer-a` | `peer-b`（クラスター内で一意） |
| ホストID | `host-a` | `host-b`（物理PCごとに一意） |
| LANのIPv4 | `192.168.1.10/24` | `192.168.1.11/24` |
| ブロードキャスト先 | `192.168.1.255` | `192.168.1.255` |
| 共通UDPポート | `47740` | `47740` |
| ログ転送TCPポート | `47741` | `47741`（各PCのIPで待受） |
| 初回の `initialize` | `true` | `false` |

ブロードキャスト先は、使用する IPv4 アドレスとサブネットマスクに合わせて指定してください。この例では `192.168.1.0/24` の LAN を使用します。複数 NIC がある PC では、CoreService と ClientHost それぞれが使用する NIC を指定してください。Interface ID は PC ごとに異なるため、各 PC で取得した値を使用してください。

## 2. 各PCへ配布物と設定を置く

配布一式をローカルディスクの `C:\Clutch-CS\package` に展開してください。各アプリは実行ファイルだけでなく、フォルダー全体を保持してください。

```text
C:\Clutch-CS\
  package\
    Clutch-CS-Setup.exe  GUIセットアップ
    files\
      core\             Coreの導入元（release-manifest.jsonを含む）
      updater\          導入確認・更新用の実行物
      client\           ClientHostと画面資材
      cli\              管理CLI
      installer\        導入スクリプト
      README.md         セットアップの案内
  config\contents.toml
  data\               このピアだけの永続データ
  content\            ワークフローで使用するファイル
  install-result\     導入確認結果・サービス登録用資材
  service\            インストーラーが新規作成するCoreの配置先
```

`package` はClientHostとUpdaterの実行元として運用中も保持します。**`service` は事前に作成しません。** 設定・データ・ログをサービス入替先の外に置くことで、更新時にも保持できます。

```powershell
New-Item -ItemType Directory -Force -Path 'C:\Clutch-CS\config', 'C:\Clutch-CS\data', 'C:\Clutch-CS\content', 'C:\Clutch-CS\install-result'

# Core設定に使用するInterface IDと、そのNICに割り当てられたIPv4を確認
[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
    [pscustomobject]@{
        Name = $_.Name
        InterfaceId = $_.Id
        IPv4 = ($_.GetIPProperties().UnicastAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.Address.IPAddressToString }) -join ', '
    }
}
```

PC Aの `C:\Clutch-CS\config\contents.toml` をUTF-8 で作成してください。下の2か所の `interfaceId` は、上で確認した同じLAN用NICのIDへ置き換えてください。

```toml
[cluster]
id = "clutch-cluster"
peerId = "peer-a"
hostId = "host-a"
interfaceId = "このPCのLAN用Interface ID"
address = "192.168.1.10"
broadcast = "192.168.1.255"
port = 47740
initialize = true

[storage]
directory = "C:/Clutch-CS/data"
backend = "sqlite"

[time]
zone = "Asia/Tokyo"

[files]
roots = ["C:/Clutch-CS/content"]
recoveryDirectory = "C:/Clutch-CS/data/file-recovery"

[logs]
directory = "C:/Clutch-CS/data/logs"
interfaceId = "このPCのLAN用Interface ID"
listenPort = 47741

[updates]
installationRoot = "C:/Clutch-CS/service"
stagingRoot = "C:/Clutch-CS/staging"
backupRoot = "C:/Clutch-CS/backups"
updaterExecutablePath = "C:/Clutch-CS/package/files/updater/ReArtWord.Contents.BackgroundService.Updater.exe"
serviceKind = "windowsService"
serviceName = "ReArtWord.Contents.BackgroundService"
```

PC Bでは同じ構成のTOMLを用意し、`peerId = "peer-b"`、`hostId = "host-b"`、`address = "192.168.1.11"`、**`initialize = false`** と、その PC の 2 か所の `interfaceId` を変更してください。各PCの保存先は同じ文字列でも構いませんが、**PC AのdataをPC Bへ複製せず、別の空の領域から開始**してください。同期によりクラスターの共有設定を受け取ります。

`initialize = true` は、空の保存領域から新しいクラスターを作る最初の 1 台だけに指定してください。参加できないPCをこの値へ変更して解決しようとしないでください。初回参加を終えた保存領域は再起動にもそのまま使います。識別子を変更して既存領域を流用すると起動を拒否します。NIC・IP変更には明示的な再選択が必要です。

画面なしで新規クラスターへ初期定義を導入する場合は、TOMLの最初のセクションより前に `workflows = ["workflows/startup.yaml"]` のようなトップレベル配列を置けます。相対パスはTOMLのあるディレクトリから解決します。`initialize = true` の初回導入に使用し、通常再起動ではファイルの変更を保存済み定義へ反映しません。参加ピアは共有定義を受け取り、その後の更新は画面または管理APIで保存します。

保存方式・保存不能時の扱いなど、ほかの起動設定は[運用時の動作](../reference/cluster-operations.md#用途別保存と保存不能時の扱い)を参照してください。

## 3. 使用するLANの通信を許可する

この手順で使用する Install-Offline.ps1 は、NIC・IP・Firewall の設定を変更しません。GUI のセットアップとは異なり、通信許可は手動で設定してください。管理者のPowerShell 7で、導入先のネットワークポリシーに合わせて受信を許可してください。共通UDPポートはCoreServiceだけでなく、ClientHostとUpdaterの参加確認でも受信に使います。

以下は、使用NICが `Ethernet`、ネットワークプロファイルが `Private` の場合の例です。`Get-NetConnectionProfile` で実際の名前とプロファイルを確認し、`$lanInterface`・`$lanProfile`・`$lanSubnet` を置き換えて各 PC で一度実行してください。

```powershell
Get-NetConnectionProfile
$lanInterface = 'Ethernet'
$lanProfile = 'Private'
$lanSubnet = '192.168.1.0/24'

New-NetFirewallRule -Name 'Contents-Cluster-UDP' -DisplayName 'Clutch-CS cluster discovery and management' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 47740 -InterfaceAlias $lanInterface -Profile $lanProfile -RemoteAddress $lanSubnet
New-NetFirewallRule -Name 'Contents-Logs-TCP' -DisplayName 'Clutch-CS log forwarding' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47741 -InterfaceAlias $lanInterface -Profile $lanProfile -RemoteAddress $lanSubnet
```

送信も制限している環境では、同じLANへのUDP `47740`とTCP `47741`を許可します。UDPの送信元ポートは固定ではありません。ClientHostのHTTP `5081`は端末内の `127.0.0.1` で待ち受けるため、LANへ公開しません。HTTP／OSCなどのワークフローを追加した場合は、そのノードで使うポートを別途確認してください。

共有状態の同期本文は、設定したクラスター用NIC・IPv4上の一時TCPポートで転送します。各CoreServiceの実行ファイル、ローカルIPv4、同じクラスターの相手IPv4に限定してTCP受信を許可してください。送信を制限している場合は、その相手へのTCP送信も必要です。ログ用のTCP `47741`の許可だけでは同期本文のTCP転送はできません。TCP接続を開始できない環境ではUDP断片転送へ戻るため、大容量の再参加同期は遅くなる場合があります。

TCP接続後の途中切断や長さ・ハッシュの不一致では本文を取り込まず、同期の完了として扱いません。

## 4. PC Aをインストールし、クラスターを作成する

ダウンロードした配布物のスクリプトが実行ポリシーで拒否される場合は、配布元と内容を確認したうえで、導入用セッションだけを `RemoteSigned` にし、対象の 3 本をブロック解除してください。組織が設定した実行ポリシーがある場合は、その運用に従って署名済みスクリプトを用意してください。

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
Unblock-File -LiteralPath 'C:\Clutch-CS\package\files\installer\Install-Offline.ps1', 'C:\Clutch-CS\package\files\installer\Install-ServiceActions.ps1', 'C:\Clutch-CS\package\files\installer\Set-WindowsServiceRecovery.ps1'
```

**PC A で**次を実行してください。TOMLと配布物の検証後、CoreServiceを配置し、Windowsサービスを自動起動で登録・開始します。

```powershell
& 'C:\Clutch-CS\package\files\installer\Install-Offline.ps1' `
    -PayloadRoot 'C:\Clutch-CS\package\files\core' `
    -InstallationRoot 'C:\Clutch-CS\service' `
    -ConfigurationPath 'C:\Clutch-CS\config\contents.toml' `
    -ServiceArtifactRoot 'C:\Clutch-CS\install-result' `
    -Platform Windows `
    -RegisterAndStart `
    -VerifierExecutablePath 'C:\Clutch-CS\package\files\updater\ReArtWord.Contents.BackgroundService.Updater.exe'

Get-Service -Name 'ReArtWord.Contents.BackgroundService'
Get-Content 'C:\Clutch-CS\install-result\installation-result.json' -Raw
```

成功時は **`Registered, started and verified Ready: clutch-cluster/peer-a.`** と表示され、結果JSONの `success` が `true`、`stage` が `ready` になります。これはファイル配置だけでなく、配置した実行物の今回の起動・版・ピアID・UDPによる参加状態まで確認した結果です。サービスの `Running` だけでは参加完了と判断しません。

既定の待機時間は60秒です。`-RegisterAndStart` を省略した場合は、配置と登録用資材の生成までで終了し、サービスは登録・起動されません。

## 5. PC Bをインストールし、既存クラスターへ参加する

PC A が `Ready` で稼働していることを確認してください。**PC Bで**、手順2の追加ピア用TOMLと手順3の通信設定を用意し、手順4と同じインストールコマンドを実行してください。

PC Bは `initialize = false` で起動し、同じIDのクラスターを発見して共有状態を同期します。**`clutch-cluster/peer-b` の `Ready` が確認されたら参加完了**です。追加の「参加要求」ボタンや `cluster rejoin` の実行は必要ありません。`cluster rejoin` は、過去に明示離脱させたピアを再参加させる操作です。

3台目以降も、クラスターID・ブロードキャスト先・UDPポートを合わせ、ピアID・ホストID・IP・NIC・保存領域をそのPC用にして同じ手順で追加します。同じ版のCoreService／ClientHostを配置してください。

## 6. 画面を起動し、参加状態を確認する

画面を使うPCで、通常ユーザーの PowerShell から ClientHost を起動してください。管理専用PCならCoreServiceのインストールは不要で、配布一式の `files/client`、LANの接続設定、共通UDPの受信許可を用意します。

```powershell
Start-Process -FilePath 'C:\Clutch-CS\package\files\client\Clutch-CS.ClientHost.exe' -ArgumentList '--listen http://127.0.0.1:5081/' -WindowStyle Hidden -PassThru
```

ブラウザーで **<http://127.0.0.1:5081/dashboard>** を開き、次の順に操作してください。画面の既定言語は英語です。

1. 右上の **Select connection** を開いてください。
2. **NIC** と **IPv4 on this interface** に、画面を動かしているPC自身のLAN 用 NIC・IP を選択してください。PC Aから見る場合はPC AのIPです。
3. **Broadcast address** を `192.168.1.255`、**UDP port shared with Core** を `47740` にし、**Add or update this network** を押してください。
4. 観測された `clutch-cluster` の `peer-a` または `peer-b` にある **Connect to this cluster and peer** を押してください。
5. Dashboardの **Selected cluster** で `peer-a` と `peer-b` の両方が `Ready`、ホストが 2 台であることを確認してください。WorkflowEditorの上段にはクラスター、下段にはそのクラスターのワークフローが表示されます。

新規クラスターにワークフローがないのは正常です。WorkflowEditorで作成し、**Save & share** で保存・共有してください。保存・共有・有効化の結果を確認してから、もう一方のピアでも同じ定義を取得できることを確認できます。

ClientHostの接続設定は、既定でユーザーの `%LOCALAPPDATA%\ReArtWord\Contents\Client` に保存されます。明示する場合は `--state <保存先>` を指定してください。CoreServiceのサービス起動だけではClientHostは起動しません。ブラウザーに画面資材が保存されていると、ClientHost停止中も画面自体は開きますが、クラスター一覧や実測値は取得できません。GUIやClientHostを閉じてもCoreServiceは継続します。

Windowsでログオン時にDashboardの監視を復元する場合は、実際に使用するユーザーのPowerShell 7で次を実行してください。既に選択したNICを保持するため、普段の `--state` と同じ保存先を指定してください。登録されるタスクは一般ユーザーの対話セッションでClientHostを非表示起動し、昇格や実行ポリシー変更を要求しません。別ユーザー向けに管理者が登録する場合は `-User <ユーザー名>` を明示してください。

```powershell
& 'C:\Clutch-CS\package\files\installer\Register-ClientStartup.ps1' `
  -ExecutablePath 'C:\Clutch-CS\package\files\client\Clutch-CS.ClientHost.exe' `
  -StateDirectory "$env:LOCALAPPDATA\ReArtWord\Contents\Client"
```

手動で開く場合は同じ引数を `Start-Client.ps1` に渡してください。Start-Client.ps1 は実行ファイルと保存先を確認し、ClientHost が起動していなければ起動を待ってからブラウザーを開きます。配布物の場所を変えた場合は、自動起動の登録も新しい実行物の場所へ更新してください。

複数NICから独立したクラスターをまとめて見る場合も、ClientHostのHTTP URLは一つにできます。**Select connection** で各NIC・IPv4・ブロードキャスト・UDPポートを指定し、**Add or update this network** で追加してください。設定済みの他NICの監視は続きます。**Stop discovery** はその割当だけを解除します。

各クラスターの専用NICは維持され、管理要求・内部状態操作・元要求の結果照会も、対象ピアを一意に観測したNICへ送ります。同じクラスター・ピアへの複数経路や複数実体が観測される場合は、暗黙に経路を選びません。

**All clusters** の **Cluster health** 一覧には、クラスターごとの稼働状態、Readyピア数、今回確認できたピア数、コーディネーター、最終確認時刻を表示します。単一・全クラスター概要とも一度に128ピアまで状態を照会し、複数のクラスターを同じページで確認できます。状態照会の応答と実体が一致した結果だけを確認済みとし、古い値・取得失敗・128ピアの取得範囲外は未確認です。

発見後に見えなくなったクラスターは1分間保持して通信不能を示します。

複数NICの割当は状態ディレクトリ内の `networks.json` にJSON配列で保存します。初回配置時に同ファイルを用意することもできます。最大32件で、同一NICとUDPポートの組は一件までです。未検出のNICはその割当だけが利用不能になり、他の監視を続けます。

```json
[
  { "interfaceId": "このPCのNIC-A-ID", "bindAddress": "192.168.10.10", "broadcastAddress": "192.168.10.255", "port": 47740 },
  { "interfaceId": "このPCのNIC-B-ID", "bindAddress": "192.168.20.10", "broadcastAddress": "192.168.20.255", "port": 47740 }
]
```

`GET /client/network` の `bindings` で全割当の状態を取得でき、`PUT /client/networks` は同形式の配列で全割当を置換します。CLIの `network set` は、明示された一件への全置換として動作します。

オフライン編集を使う前に、配布版の画面下部で **UI assets: Offline ready** を確認してください。取得・保存した完全な定義と端末の草稿を利用できます。新しいUIが待機している場合は、草稿を保管して同じClientHostの画面をすべて閉じ、開き直します。

## うまく進まないとき

PC再起動後の確認と、予期しない終了からの復旧設定は、次の「PCの再起動・異常終了から復旧する」を参照してください。

| 状況 | 確認する箇所 |
| --- | --- |
| 配布アプリが起動しない | 対象RIDと配布フォルダー全体が揃っているか。自己完結型の一式を使っているか。ClientHostは同じコマンドを前景で実行して起動メッセージを確認。 |
| 登録前の検証で止まる | 例外に表示される検証結果JSONの `stage` を確認。`configuration` はTOML、`platform` はRID・期待版、`payload` は配布物、`network` はNIC/IP、`files` は許可パス。 |
| 一覧にクラスターが出ない | CoreServiceの起動、ClientHost自身のNIC/IP、ブロードキャスト先・UDPポート、選択NICのFWプロファイルと受信許可、同じブロードキャスト到達範囲か。 |
| 追加ピアが `Syncing` のまま | 同じクラスターの既存 `Ready` ピアが動いているか。ID・通信設定・保存領域の権限と、Dashboardの同期理由を確認。新規作成へ切り替えない。 |
| ログだけ取得できない | 各PCの `[logs]` のNIC、TCP待受ポート、FW受信許可を確認。 |
| `InstallationRoot` が既存で拒否される | 新規インストール用の保護。既存配置は更新手順を使用してください。初回失敗後は下記の結果を確認してください。 |

登録・起動後に失敗した場合は、`install-result\installation-result.json` の `stage`（`register`／`recovery`／`start`／`ready`）と、同じフォルダーの検証結果を確認してください。ファイルと登録済みサービスは保持され、自動再実行・削除は行いません。通信待ちなら先行PCやFWを修正して状態を再確認し、起動設定を直した場合は、設定を確認してから対象サービスを再起動してください。

同じインストールコマンドを繰り返したり、復旧のためにデータを消したりしないでください。[導入・更新の詳細](../reference/update-process.md)と[管理CLI](../reference/command-line.md)も参照できます。

## PCの再起動・異常終了から復旧する

通常の再参加が進まない場合や、更新途中の旧版復帰・共有状態の手動修復が必要な場合は、[緊急復旧手順](troubleshooting.md)を参照してください。

通常のPC再起動では、OSサービスがCoreServiceを自動起動します。保存したピア／ホストの識別、NIC割当、共有設定、実稼働ワークフローを読み直し、同期と必要条件を確認してから `Ready` へ戻ります。以前の `Ready` をそのまま復元しません。同じNIC・IPがブート時にまだ使えない場合は、その割当で待機し、回復後に再開します。別NICへの切替や、失敗・結果不明だった一回の処理の再実行は行いません。

新規インストール時には、次のOS側復旧設定も登録します。

| OS | PC起動時 | 予期しない終了時 |
| --- | --- | --- |
| Windows | Automaticサービスとして開始 | 初回5秒、次回15秒、以後60秒後に再起動。失敗回数は失敗のない期間が24時間続くとリセット。CoreService内部の致命的エラーも非ゼロのサービス終了として通知 |
| Linux | systemdでenableしたサービスを開始 | `Restart=on-failure`、`RestartSec=10s`。起動回数制限で復旧が永久停止しないよう `StartLimitIntervalSec=0` |
| macOS | launchdの `RunAtLoad` で開始 | `SuccessfulExit=false` のKeepAliveで非正常終了時に再起動。`ThrottleInterval=10` で連続起動を抑制 |

通常のサービス停止・更新時の正常停止では、自動再起動を要求しません。クラスターからの**明示離脱（Detached）はPC再起動後も維持**し、再参加には明示操作が必要です。ClientHostはCoreServiceと独立しており、画面を使う際に起動します。

各OSのサービス登録内容は[インストーラーの説明](../../installer/README.md)を参照してください。

Windows上では実プロセスの再起動・状態復元と、サービス終了コードの通知順を試験しています。実PCの再起動、SCMによる自動再起動、Linux／macOSでのサービス managerの実動作は、対象環境での確認が必要です。
