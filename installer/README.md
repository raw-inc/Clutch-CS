# セットアップ用スクリプト

[ドキュメント一覧](../docs/README.md)

通常のインストールには、配布物の **Clutch-CS-Setup.exe** を使用してください。
このフォルダーには、配布物の生成や、設定ファイルを使う導入・保守のスクリプトがあります。

| 目的 | スクリプト | 手順 |
| --- | --- | --- |
| インストーラー一式を生成する | `New-InstallPackage.ps1` | [配布物を作成する](../docs/development/distribution.md) |
| 対応ソース ZIP を生成する | `New-SourcePackage.ps1` | [対応ソースの生成](../docs/development/distribution.md#対応するソースを生成する) |
| 配布ファイルのマニフェストを生成する | `New-ReleaseManifest.ps1` | [サービス導入スクリプトの仕様](../docs/reference/service-installation.md) |
| 設定ファイルから導入する | `Install-Offline.ps1` | [スクリプトによる導入](../docs/guides/manual-install.md) |
| ClientHost を起動する | `Start-Client.ps1` | [スクリプトによる導入](../docs/guides/manual-install.md) |
| ClientHost の自動起動を登録する | `Register-ClientStartup.ps1` | [スクリプトによる導入](../docs/guides/manual-install.md) |
| Windows サービスの回復設定を変更する | `Set-WindowsServiceRecovery.ps1` | [サービス導入スクリプトの仕様](../docs/reference/service-installation.md) |
| RemoteDesktop のトレイ表示を設定する | `Set-WindowsDesktopTrayPreference.ps1` | [トレイ表示の設定](../docs/reference/service-installation.md#remotedesktopのトレイ表示設定) |

導入スクリプトは PowerShell 7 を使用します。
管理者権限の要否、既存配置の扱い、確認結果の保存先は各手順を確認してください。

配布物内の `README.md` には、利用者向けの [package-readme.md](package-readme.md) を使用します。
