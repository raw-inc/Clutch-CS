# プロセス・リソースノード

[ノード一覧](README.md) · [サンプル](../../../samples/generic-process-resources/README.md)

## 登録と共通設定

CoreService には、プロセス管理・リソース監視のノードと収集処理が組み込まれています。共通の既定値は TOML の `[processes]` と `[resources]`、対象のアプリケーションは Dashboard の Applications タブで設定します。

自動復旧とスケジュールは[アプリケーションの登録・監視](../applications.md)、CoreService 自体の再起動設定は[セットアップと配布](../../../installer/README.md)を参照してください。以下の C# は、独自のホストへ組み込む場合の例です。

実行するピアに `Infrastructure.Processes.ProcessService(IDurableRepository, peerId, ProcessOptions)` を配置し、`ProcessNodeRegistration.Create(IProcessService, IWorkflowEvents)` でノードを登録します。プロセス操作には、登録したアプリケーションの ID を指定します。

```csharp
var processes = new ProcessService(repository, peerId,
    new ProcessOptions(DefaultRecoveryAttempts: 3,
        DefaultReadyTimeoutMilliseconds: 30000,
        RecoveryDelayMilliseconds: 1000, PollMilliseconds: 250));
var definition = new ProcessDefinition("renderer", EProcessMode.Managed,
    "D:/Applications/renderer.exe", ["--mode", "interactive"],
    "D:/Applications",
    Recovery: new ProcessRecoveryPolicy(MaximumAttempts: 3));
await processes.RegisterAsync(definition);
```

型は `Contracts.Processes`、インターフェースは `Runtime.Processes`、生成処理は `Nodes.Processes`。共通初期値は有限であり、`ProcessOptions` の nullable limitにnullを明示すれば共通既定そのものを無制限へ変更できます。個別 `ProcessRecoveryPolicy.Unlimited=true` も選べる。

再試行回数や待機時間は、対象アプリケーションの起動・停止にかかる時間に合わせて調整してください。

`ListAsync` は `RegisteredProcess(Version, Definition)` を返します。変更時は確認したVersionを `RegisterAsync(updatedDefinition, expectedVersion)` へ渡します。古い版の更新は成功させない。登録更新は実在確認を保存時に強制せず、パス・権限・実行物の確認は実操作のピアで行います。

既存プロセスを観測するには、PIDとOS起動時刻の両方を `ProcessIdentity` に指定し、`Mode=ObserveOnly` で登録します。このモードでは人の直接操作・人が起動したワークフローでもStart/Stop/Recoverを拒否します。操作が必要なら登録版を確認してManagedへ変更します。PIDの再利用は元のプロセスの生存と同一視しません。

## ノードと実行境界

プロセスノードは実行先ピアに登録されたプロセスを操作します。共通設定の `processId` に登録IDを指定し、必要なPCをPC選択ノードまたは既存のピア指定で選択します。実行PCが未指定の場合は最優先PCを使用します。

| type | 追加設定／入力 | 挙動 |
|---|---|---|
| `process.start` | なし | 登録済み引数をArgumentListで起動。既に同じプロセスが生存していれば再起動しない |
| `process.restart` | `process.stop`と同じ停止方針・待機上限 | 終了を確認してから一度だけ起動。停止失敗では追加起動しない |
| `process.lifecycle` | 登録アプリケーション | 起動・停止・再起動信号、事象詳細、PIDを出力。監視開始時の現在状態だけでは発火しない |
| `process.stop` | `failurePolicy: force / waitForManual` 必須、`gracefulTimeoutMs` 任意（既定30000） | 通常終了を要求して待ちます。人待ちは動作を残して失敗、forceは指定プロセスを強制終了し実終了を確認 |
| `process.observe` | なし | 現状態を一回取得し、同じ異常終了を二重計上しない |
| `process.watch` | `autoRecover`（既定true）、`intervalMs` | 状態変化を継続出力し、Managedの確認済み復旧条件だけを評価します。`process.changed`共通フックも発行 |
| `process.waitReady` | `timeoutMs`任意、nullは明示無期限 | 生存だけをreadyへ丸めず、現起動回のready=trueまで待ちます。未指定は共通の既定上限 |
| `process.signal` | 値に`{instanceId,ready,healthy,stable}` | 汎用ワークフローが確認した準備／利用可能／安定状態を現起動回へ供給。古いinstanceIdを拒否 |
| `process.recover` | なし | 確認済みの異常だけを登録復旧条件内で処理する |
| `process.recoverySuppression` | `suppressed`必須boolean | 人が外部停止する前の抑止を永続保存し、解除まで復旧しない |
| `process.confirmRecovery` | なし | 人が故障の区切りを確認し回数をリセットする |

起動の成功はOS プロセスを作成して状態を保存できたことを表し、アプリケーションのready成功ではありません。通信受信・条件判定などで得た状態を、起動結果の `instanceId` と照合して `process.signal` へ渡します。`ready` / `healthy` のnullは判定不能であり、異常成立ではありません。準備完了後にだけ次の処理を始める場合は `process.waitReady` を間に配置します。

故障回数、復旧試行数、人の抑止、予定停止意図はピアローカルの共通リポジトリへ保存します。復旧試行数は、自動復旧を含むアプリケーションの起動成功時に0へ戻します。サービス／クラスターの再起動や日付変更だけではリセットしません。現起動回の明示 `stable=true` または人の `ConfirmRecoveryAsync` では、故障回数と復旧試行数をリセットできます。stableは実生存を確認できる起動回だけに適用します。

生存中の確認済み利用不能も復旧する場合は、登録 `Recovery.RecoverWhenUnhealthy=true` と `UnhealthyStopPolicy` を明示します。通常終了の上限は `StopTimeoutMilliseconds`（既定30000）。未知の通信状態を復旧条件へ広げない。上限到達後は `RecoveryExhausted=true` となり、watch出力とフックから通知・後続処理へつなぐ。

`process.watch`の`value`は観測状態、`recovery`は復旧操作自体の成否です。

起動前に起動意図を保存し、起動後のPID保存に失敗したときは結果不明として二重起動を阻止します。`ResolveLaunchAsync(id, confirmedIdentity)` は人が現物確認した既存PIDへ紐付ける操作で、確認済み未起動ならnullを明示します。通常の故障回数リセットとは別の操作です。

Windowsの通常停止は [Process.CloseMainWindow](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.closemainwindow) による終了要求です。GUI終了要求を受け付けないconsoleアプリケーションでは、明示した停止後方針が適用されます。forceの対象は登録したプロセス自身であり、未登録の子プロセスまで暗黙に管理対象へ追加しません。

Linux上の穏当停止プロトコル・実機権限は未検証であり、アプリケーション固有の停止要求は通常の通信ノードから先に送れる。

## Resource と共通診断履歴

`Infrastructure.Resources.ResourceService(IDurableRepository, peerId, ResourceOptions)` と `Nodes.Resources.ResourceNodeRegistration.Create(IResourceService)` を使用します。

ホストは `ResourceService.RunAsync(hostStopToken)` をワークフローの有無とは独立して起動し、終了時に待機してからDisposeします。

```csharp
var resources = new ResourceService(repository, peerId,
    new ResourceOptions(SampleIntervalMilliseconds: 3000,
        HistoryCapacity: 720, HistoryRetentionHours: 24,
        NvidiaSmiPath: null));
```

`resource.observe` は現在値を一回取得し、`resource.watch` は `intervalMs`（既定5,000ms）ごとに取得します。どちらも選択したPCで動作し、未指定なら最優先PCを使用します。各測定項目には `name`、`target`、`value`、`unit` と、取得できない場合の `reason` があります。取得できない値はnullのまま返し、ゼロとして扱わない。

CoreServiceの共通収集は既定3,000msで、`resource.watch` の既定5,000msとは別です。CoreService起動時のTOMLで次のように指定できます。既存設定の明示値は維持されます。

```toml
[resources]
sampleIntervalMilliseconds = 3000
historyCapacity = 720
historyRetentionHours = 24
# nvidiaSmiPath = "C:/Program Files/NVIDIA Corporation/NVSMI/nvidia-smi.exe"
```

ノードの `value` は `id`、`peerId`、`observedAt`、`metrics`、`historyStored`、`instanceId`、`selectedInterfaceId` と、保存待ちの場合の `historyPending` を含みます。`metrics`ポートは測定名と対象をキーにした辞書で、たとえば `/cpu.usage/machine/value` をJSON Pointerで抽出できます。

`historyPending=true` はメモリ内の保存待ち、`historyStored=true` は保存確認済みです。`historyStored=false` だけで保存失敗と判定しません。実体やNICが異なる標本を連続した速度計算へ流用しません。

| 観測 | 具体取得元／範囲 |
|---|---|
| CPU使用率 | Windows `GetSystemTimes` の差分、Linux `/proc/stat`、macOSの `top`。初回差分待ちはnull。Windows複数processor groupは全体値と誤認させず取得不能理由を返す |
| 物理メモリ | Windows `GlobalMemoryStatusEx`、Linux `/proc/meminfo`、macOSの `sysctl hw.memsize` と `vm_stat` から総量・利用可能量・使用量・使用率を取得 |
| ドライブ | 固定ドライブとリムーバブルドライブの容量・空き容量。ネットワークドライブには自動でアクセスしない |
| ネットワーク | NIC識別子ごとの累積受信・送信バイト数。画面の通信速度は連続した観測の差分から求め、初回・欠測・カウンターのリセットを区別する |
| Windows GPU | OSの性能カウンターとアダプター情報から、GPUごとの使用率・専用VRAM・共有システムメモリを取得。AMD・Intel・NVIDIAのうちOSが情報を提供するアダプターを対象にする |
| NVIDIAの追加情報 | `NvidiaSmiPath` に実行ファイルを指定した場合、CLIでGPUごとの使用率・メモリ・温度を照会。取得不能や非対応はnullと理由を返す |

CPU・メモリなどの取得処理は、[GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)、[MEMORYSTATUSEX](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)、[Linux proc](https://docs.kernel.org/filesystems/proc.html)、[NVIDIA SMI](https://docs.nvidia.com/deploy/nvidia-smi/) に対応しています。

WindowsのGPU取得処理は [WindowsGpuProbe](../../../src/ReArtWord.Contents.BackgroundService.Infrastructure/Resources/Gpu/WindowsGpuProbe.cs) を参照します。内蔵GPUの専用領域はOSが予約したシステムRAMの場合があり、共有メモリを専用VRAMへ加算しません。温度の取得先がない場合も欠測理由を返します。

履歴は `peer:{peerId}:resources` の有限リングとしてメモリに保持し、件数と保持期間の両方を適用します。定期採取は初回と約30秒ごとにまとめて保存し、通常停止時も残りの保存を試みる。強制終了・電源断では未保存区間を失う場合があります。`resource.observe`／`resource.watch` や管理APIの明示採取はその都度保存します。`LocalJsonSnapshots` を通じた原子的置換を使い、履歴本文を操作記録や共有複製へ蓄積しません。

破損履歴は暗黙初期化せず、現在値と保存できない状態を返します。

Dashboardはバックグラウンド収集の最新標本を参照するため、画面更新では追加採取・履歴保存を行わない。ClientHostのグラフ用キャッシュとCoreServiceの診断履歴は保持期間・用途が異なります。[画面側の観測範囲](../web-client.md#dashboardの状態保持)を参照します。CPU温度は現在の標準収集に採取元がなく、ゼロとして補わない。

`resource-warning.yaml` は編集・確認前に警告を発しない無効状態の例です。リソース観測の後に通常の抽出・条件・ログを接続します。警告ワークフローのWARN ログは共通診断履歴の代替ではありません。

CoreServiceのリソース計測履歴は、保持件数・期間を維持したまま32件以下の区間に分けて保存します。SQLiteでは変更区間と索引だけを書き換えます。旧形式の履歴は初回保存時に移行し、新索引の保存が完了するまで旧履歴を保持します。不要になったSQLiteページは再利用されるため、DBファイルの見かけのサイズが直ちに縮むとは限りません。旧版へ戻した場合、区間形式の診断履歴は表示できませんが、設定やワークフローの保存形式には影響しません。

## 検証した範囲

Windowsの実子プロセスと永続SQLiteで登録管理、観測のみ、ready待ち、PID再利用保護、障害が起きる段階ごとの動作、復旧上限、抑止、予定停止、実強制終了を検証済みです。実CPU・物理メモリ、共通履歴の再読取・件数・保持期間・破損時の現在値継続、ワークフローなしの収集、source正常終了を検証済みです。

Windows では NVIDIA GPU の使用率・専用 VRAM・共有メモリを実画面で確認済みです。管理APIとPC選択は、同じPC上の別CoreServiceプロセス間の通信で確認しています。AMD・Intelの実機、Linux・macOSでの実測、別の物理PCやNICの故障時動作は未確認です。最大想定の二倍負荷を使う性能試験は、実構成と負荷条件を定めて実施してください。
