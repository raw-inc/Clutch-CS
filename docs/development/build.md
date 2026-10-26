# 開発環境とビルド

[ドキュメント一覧](../README.md) · [ソース構成](architecture.md) · [配布物の生成](distribution.md)

以下のコマンドは、PowerShell 7 でリポジトリのルートから実行してください。
ソリューション名は **Clutch-CS.slnx** です。

## 必要なツール

| ツール | 用途 |
| --- | --- |
| .NET 10 SDK・wasm-tools | C#、Web クライアントのビルド |
| PowerShell 7 | ビルド・配布スクリプト |
| Node.js 24 | JavaScript・CSS の静的解析とテスト |
| Git | ソース管理、共通アナライザーの取得 |
| Python 3 | 一部の結合試験、Skia のビルド |
| C++ ツールチェーン | Windows 配布物の Native AOT と Skia のビルド |

Windows の配布物を生成する場合は、Visual Studio の「C++ によるデスクトップ開発」、Windows SDK、CMake ツールに含まれる Ninja も必要です。
OS 固有機能の検証は、対象 OS で実施してください。

## 通常の開発環境を用意する

~~~powershell
dotnet workload install wasm-tools
npm ci --ignore-scripts
dotnet restore Clutch-CS.slnx
./build.ps1 -Configuration Release
~~~

ビルドでは、共通 SourceCodeQuality Core Analyzer、Blazor 用の Razor Analyzer、Avalonia 用の MVVM Analyzer の **1.0.1** を使用します。
アナライザーは restore 時に[公開リポジトリ](https://github.com/raw-inc/ReArtWord.CSharp.SourceCodeQuality.Analyzers)の `main` から取得され、[accepted-commit.txt](../../eng/analyzers/source-quality-core/accepted-commit.txt) に記録したコミットが使用されます。専用の認証情報は不要です。
取得エラーや診断が出た場合は、アナライザーを有効にしたまま原因を解消してください。

ビルドには `Version.props` に指定したバージョンを使用します。更新方法は[バージョン管理](versioning.md)を参照してください。

## 変更に対応するテストを実行する

~~~powershell
dotnet test tests/ReArtWord.Contents.BackgroundService.Unit.Tests -c Release --no-build --no-restore --filter 'Category!=Performance'
dotnet test tests/ReArtWord.Contents.BackgroundService.Integration.Tests -c Release --no-build --no-restore --filter 'Category!=Performance'
dotnet test tests/ReArtWord.Contents.BackgroundService.Client.Tests -c Release --no-build --no-restore --filter 'Category!=Performance'
dotnet test tests/ReArtWord.Contents.BackgroundService.Installer.Tests -c Release --no-build --no-restore
~~~

変更した機能と障害時の動作に対応する試験を選択してください。対象を絞る場合は `--filter 'Category!=Performance&FullyQualifiedName~テストクラス名'` のように条件を組み合わせてください。
実通信を使う試験群は、ポートや計算資源を競合させないよう順番に実行してください。

Installerの実配置試験は、[生成した配布物](distribution.md)の `files` フォルダーを指定して実行してください。ローカル／接続先WebClientの更新とユーザー自動起動の試験には、サインイン中のWindowsセッションが必要です。

~~~powershell
$env:CLUTCH_TEST_PACKAGE = (Resolve-Path artifacts/package/files).Path
dotnet test tests/ReArtWord.Contents.BackgroundService.Installer.Tests -c Release --no-build --no-restore --filter 'FullyQualifiedName~RealInstallationTests|FullyQualifiedName~RealWebClientTests|FullyQualifiedName~RealWebClientUpdateTests'
~~~

CIでも配布物生成後に同じ試験を選択します。Session 0ではユーザー自動起動を伴う試験がスキップされ、未確認の試験名と理由がTRXと「Installation coverage」サマリーに表示されます。

CIの結合・性能試験では、リリースと同じReadyToRun方式で発行したCoreServiceを使用します。Windowsで同じ起動条件を確認する場合は、次を実行してから対象の試験を選択してください。通常のビルド出力へ戻す場合は `CONTENTS_TEST_CORE` を解除してください。

~~~powershell
dotnet publish src/ReArtWord.Contents.BackgroundService.CoreService -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -warnaserror -o artifacts/test-core
$env:CONTENTS_TEST_CORE = (Resolve-Path artifacts/test-core/Clutch-CS.CoreService.exe).Path
~~~

切断・再試行の期限や応答時間を実時間で検証する単体試験は、`TimingSensitive` コレクションに置いてください。このコレクションは他の試験と並列実行されません。通知回数などを確認する場合は、固定時間の待機ではなく対象の処理完了を待ってください。

FFmpeg を使う映像・音声試験では、実行ファイルの絶対パスを設定してください。

~~~powershell
$env:CONTENTS_TEST_FFMPEG = 'C:/tools/ffmpeg/bin/ffmpeg.exe'
$env:CONTENTS_TEST_FFPROBE = 'C:/tools/ffmpeg/bin/ffprobe.exe'
~~~

実デスクトップ試験は、画面と音声出力デバイスが利用できる Windows の対話セッションで実行してください。通知アイコンには Windows Shell、画面取得には Desktop Duplication 対応環境、GPU 実測には対応する GPU が必要です。GitHub ホステッドランナーでは、利用できない画面機能の試験が理由付きでスキップされます。

RemoteDesktopの閲覧端末判定・時刻プローブ・正常切断の実プロセス試験も、接続時に音声取得を開始するため、同じ実機条件が必要です。デバイスを使わない閲覧端末判定や時刻プローブの通信処理は、単体試験で検証します。

- CI での検証：[公開せずに CI を手動実行する](distribution.md#公開せずにciを手動実行する)
- JavaScript・CSS：[JavaScript の静的解析](javascript.md)・[CSS の静的解析](css.md)
- 分散処理・負荷・復旧：[性能・復旧試験](performance-tests.md)
- 実 NIC を使う疎通：[2 NIC の手動試験](../../tests/ReArtWord.Contents.BackgroundService.Integration.Tests/Networking/README.md)

### Windowsの権限別に検証する

権限によって動作が変わる機能は、通常のPowerShellと「管理者として実行」したPowerShellの両方で検証してください。管理者権限と、画面・音声・サインインの有無は別の実行条件です。

クリップボード試験は、利用者のクリップボードを保護するため、専用の領域を作成します。この領域の作成には管理者権限が必要です。端末の権限制御と併せて確認する場合は、それぞれのPowerShellで次を実行してください。

~~~powershell
$env:CONTENTS_TEST_PRIVATE_CLIPBOARD = '1'
dotnet test tests/ReArtWord.Contents.BackgroundService.Unit.Tests -c Release --no-build --no-restore --filter 'FullyQualifiedName~DesktopClipboardTests|FullyQualifiedName~TerminalIdentityTests' --logger trx
~~~

非昇格時は、管理者端末の起動が拒否されることを確認します。昇格時は、管理者権限での端末起動設定と専用領域でのクリップボードの読み書きを確認します。実行時の権限が試験の条件と合わない場合は、理由付きでスキップされます。CIは単体試験でクリップボード試験を有効にし、ランナーの管理者権限・SYSTEM・セッション番号をログとサマリーに記録します。実行できた条件とスキップ理由はTRXでも確認してください。

## ローカルで起動する

CoreService の設定は[スクリプトによる導入](../guides/manual-install.md)を参考に、開発用の PC・ポート・保存先を指定してください。

~~~powershell
dotnet run --project src/ReArtWord.Contents.BackgroundService.CoreService -- --config C:/Clutch-CS/config/contents.toml
~~~

別のターミナルで ClientHost を起動できます。

~~~powershell
dotnet run --project src/ReArtWord.Contents.BackgroundService.ClientHost -- --listen http://127.0.0.1:5081/
~~~

`dotnet run` の開発用画面ではオフラインキャッシュを使用しません。
PWA と画面更新を検証する場合は、発行した資材を使用してください。

## 公開ソース ZIP からビルドする

Release のバイナリと同じ版の `Clutch-CS-Source-vx.y.z.zip` を展開してください。
`SOURCE-INFO.json` に対応する Release 名とコミットが記録されています。

公開ソースも、通常の開発と同じアナライザーを使用してビルドします。
前述のツールを用意し、依存パッケージとアナライザーを取得できるネットワーク環境で、次を実行してください。

~~~powershell
dotnet workload install wasm-tools
npm ci --ignore-scripts
dotnet restore Clutch-CS.slnx
dotnet build Clutch-CS.slnx -c Release --no-restore -warnaserror
~~~

インストーラーの生成、Skia のビルド記録、対応ソースの提供は[配布物を作成する](distribution.md)を参照してください。
