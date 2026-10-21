# サービス導入スクリプトの仕様

[ドキュメント一覧](../README.md) · [導入手順](../guides/manual-install.md) · [スクリプト一覧](../../installer/README.md)

サービスの登録、権限、起動確認、失敗時の結果、およびトレイ表示設定の仕様です。新しい PC へ導入する場合は、導入手順を先に参照してください。

## 導入とサービス登録

PowerShell 7 と対象 RID の発行成果物を使用します。CoreService の設定・保存領域・ログと Updater はサービスの入替ディレクトリ外へ配置します。

スクリプトの例では、実際の実行ファイル名・サービス名・設定キーを記載しています。配置先のパスとネットワーク設定は、使用する環境に合わせて指定してください。

新しいPCへの配布物準備、最初のクラスター作成、別PCの参加、画面からの確認は、[導入ガイド](../guides/manual-install.md)を参照してください。ビルドPCの `New-InstallPackage.ps1` は、CoreService／Updater／ClientHost／CLIの自己完結型の配布一式を作成します。導入先には.NET SDK／Runtimeの別途導入は不要です。ClientHostはCoreServiceとは独立して起動します。

`New-ReleaseManifest.ps1` で全ファイルのハッシュを記録します。自動登録を行う場合は CoreService の `AssemblyInformationalVersion` と一致する `-ExpectedBuildVersion` も指定します。Windows/Linux x64、Linux arm/arm64、macOS arm64 が対象です。

```powershell
./Install-Offline.ps1 `
  -PayloadRoot 'D:/offline/files/core' `
  -InstallationRoot 'D:/Clutch-CS/service' `
  -ConfigurationPath 'D:/Clutch-CS/config/contents.toml' `
  -ServiceArtifactRoot 'D:/Clutch-CS/install-result' `
  -Platform Windows `
  -RegisterAndStart `
  -VerifierExecutablePath 'D:/offline/files/updater/ReArtWord.Contents.BackgroundService.Updater.exe'
```

`-RegisterAndStart` は OS サービスへの実登録・開始を指示します。対象 OS の管理者/root 権限で実行してください。Windows は Automatic サービス、Linux は systemd unit の配置・enable・start、macOS は launchd plist の配置・bootstrap を行います。既存のインストール先や同名サービスを上書きしません。NIC/IP/Firewall は変更しません。

Windows のサービス登録前には、配置先を管理者と SYSTEM だけが変更できる ACL に設定し、配置ファイルを再検証します。外部の設定ファイルとその親も、一般ユーザーが変更できない場所に用意してください。ユーザープロファイルなど、親から配置を差し替えられる場所は拒否します。権限の確認に失敗した場合は `stage=permissions` で停止します。

生成された `Register-Service.ps1` を後から実行する場合も、`-VerifierExecutablePath` に同梱 Updater の絶対パスを指定します。

新規Windows登録では、開始前に `Set-WindowsServiceRecovery.ps1` で失敗時の再起動（5秒→15秒→以後60秒）と `failureflag=1` を設定します。生成した手動登録スクリプトも、同梱する同じhelperを使用します。回復設定が失敗した場合は `stage=recovery` で止め、サービスの開始へ進みません。既存サービスへ回復設定だけを適用する場合も、このhelperを管理者として明示実行できます。

サービスを再登録・即時再起動するものではありません。

CoreService内部の致命的エラーは、ホストを停止する前にWindowsサービスの `ExitCode=1` を設定します。これにより、正常停止と区別してSCMの回復対象になります。

[ServiceBase.ExitCode](https://learn.microsoft.com/en-us/dotnet/api/system.serviceprocess.servicebase.exitcode)と[サービス回復フラグ](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actions_flag)の契約に基づきます。

通常の停止・更新切替にはこの失敗通知を適用しません。

Linuxのunitは `Restart=on-failure`、`RestartSec=10s`、`StartLimitIntervalSec=0` を使用します。既存登録ではunitまたはdrop-inの同じ項目を更新して `systemctl daemon-reload` を行い、既に回数制限で失敗したサービスは `systemctl reset-failed` 後に明示開始します。

macOSのplistは `RunAtLoad=true`、`KeepAlive={SuccessfulExit:false}`、`ThrottleInterval=10` を使用します。既存plistを更新する場合は、停止可能なタイミングにその登録を `launchctl bootout`／`bootstrap` し直します。いずれもサービス登録に必要な権限で実施してください。

登録前に Updater の `--verify-install` で、TOML、実行環境（RID）、配布ファイル、NIC・IP、パスを確認します。登録後は、配置したプロセスの PID・起動時刻・実行パス・バージョンと、クラスターへの参加状態を照合します。Ready を確認してから導入成功とします。待ち時間は `-ReadyTimeoutMilliseconds` で指定し、既定は 60,000 ms です。

登録・開始後の失敗は `installation-result.json` に段階を記録します。配置済みファイルと登録済みサービスは保持し、自動で再登録・再起動・削除しません。失敗箇所を確認してから明示的に対処してください。

実Verifierの登録前後の出力は `verify-<ID>.result.json` にも保存します。検証失敗の例外には `stage` と結果ファイルのパスが表示されます。登録前に失敗した場合はまだ `installation-result.json` がないため、こちらでTOML・RID・ペイロード・NIC/IP・許可パスのどの確認で止まったかを調べます。

`-RegisterAndStart` を付けない既存の使い方は、検証済みファイル配置と各 OS の登録用成果物生成までです。このモードは通常参加の確認を行いません。

`-ActionInvoker` は試験・埋込み用の action 境界です。検証試験用の構成では OS 登録を差し替え、製品通信は既存 UDP 経路を使用します。差し替えた試験の成功を、その OS の実サービス登録確認とみなさないでください。

開発環境では `tests/ReArtWord.Contents.BackgroundService.Unit.Tests/Updates/Test-Installer.ps1` で三OSの成果物、回復設定のコマンド引数・終了値と破損拒否を、同じ場所の `Test-InstallFlow.ps1` で空の保存領域を使う実CoreService 2つのクラスター作成・参加、UDPによるReady、登録前の検証結果保持、登録/回復設定/開始/Readyの故障境界を検証できます。

実行境界を差し替えた試験では、本物のOSサービス登録やSCM回復を行いません。

## RemoteDesktopのトレイ表示設定

Windowsのユーザー別トレイ設定をデプロイ時に変更する補助スクリプトを同梱しています。Windowsの公開APIによる強制表示ではなく、`HKEY_USERS\<ユーザーSID>\Control Panel\NotifyIconSettings` 内の既存登録に対する `IsPromoted=1` の設定です。Windowsの内部仕様に依存し、OS更新やポリシーによる挙動の違いがあります。

```powershell
# 既定は読み取りだけ。対象には入替先のCore実行パスを指定します。
./Set-WindowsDesktopTrayPreference.ps1 `
  -CoreExecutablePath 'D:/Clutch-CS/service/Clutch-CS.CoreService.exe'

# 内容を確認後、同じ引数にApplyと未使用のバックアップ先を追加します。
./Set-WindowsDesktopTrayPreference.ps1 `
  -CoreExecutablePath 'D:/Clutch-CS/service/Clutch-CS.CoreService.exe' `
  -Apply -BackupPath 'D:/Clutch-CS/install-result/tray-before-001.json'
```

対象ユーザーは既定でログオン中のコンソールユーザーです。別ユーザーを対象にする場合は `-UserSid` を指定します。そのユーザーのプロファイルが読み込み済みである必要があり、別ユーザーの設定変更には管理者またはSYSTEM権限が必要です。SYSTEM自身のHKCUへは書き込みません。複数のCoreService配置は `-CoreExecutablePath` に配列で指定できます。

結果は `Promoted`（書込みと再読取済み）、`AlreadyPromoted`（設定済み）、`NotRegistered`（登録なし）、`UnsupportedValueKind`（予期しない値型）で区別します。プレビューでは変更候補を `WouldPromote` とします。`NotRegistered` は成功ではありません。その配置のCoreServiceがRemoteDesktopのアイコン表示を担当した後に再実行してください。

初回インストール直後には通常まだ登録がありません。未登録キーの捏造、旧GUIDなしアイコンの変更、全アプリの常時表示化は行いません。

適用前の全対象を指定ファイルへ保存し、適用結果は隣の `.result.json` に記録します。既存バックアップは上書きしません。障害時は両ファイルを確認し、未確認の操作を自動再実行しないでください。戻す場合はバックアップの `key` と実行パス・GUIDが一致することを確認し、`previousValueExists=true` の値だけ元の型と値へ戻します。元から値がなかった項目は `IsPromoted` の値だけを削除します。キーや他の値は削除しません。

設定の保存成功は、動作中のExplorerへの即時反映を保証しません。Explorerが保持している状態によって、直接変更した値が後から戻される場合もあります。次回サインイン後にも接続して表示を確認してください。スクリプトはExplorerを強制再起動せず、通知も出しません。利用者がその後Windows設定で変更した値を常駐処理で上書きすることもありません。
