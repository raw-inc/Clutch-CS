# 配布物を作成する

[ドキュメント一覧](../README.md) · [開発環境とビルド](build.md) · [バージョン管理](versioning.md)

配布物には Installer、CoreService、ClientHost、Updater、CLI、通信キャプチャ用プログラム、ライセンス表記を含めます。
ビルド用の環境は[開発環境とビルド](build.md)に従って用意してください。
発行ツールの実行には、SDKに加えて .NET Runtime 10.0.13 以降の10.0系が必要です。`dotnet --list-runtimes` の `Microsoft.NETCore.App` を確認し、不足している場合は[.NET Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)をインストールしてください。

## Windows の配布物を生成する

[バージョン管理](versioning.md)に従ってリリースするバージョンを設定し、リポジトリのルートから実行してください。出力先には、まだ存在しないフォルダーを指定してください。

~~~powershell
./installer/New-InstallPackage.ps1 -OutputRoot 'C:/build/Clutch-CS-win-x64' -ReleaseId 'v1.0.0' -Version '1.0.0' -RuntimeIdentifier win-x64
~~~

全コンポーネントに指定したバージョンが付けられます。`-Version` を省略した場合は `Version.props` の値を使用します。
CoreService と ClientHost には更新用プログラムも同梱され、ファイルのハッシュがマニフェストに記録されます。

~~~text
Clutch-CS-win-x64/
  Clutch-CS-Setup.exe
  files/
    README.md
    core/
      Clutch-CS.CoreService.exe
    client/
      Clutch-CS.ClientHost.exe
    updater/
    cli/
    installer/
    LICENSE
~~~

配布するときは、フォルダー全体を ZIP にまとめてください。
起動用 EXE は `files` 内の本体を相対パスで読み込むため、EXE だけを別の場所へ移動しないでください。

## ネイティブライブラリを用意する

Skia は、DNG SDK と piex を無効にした固定版のソースからビルドします。
初回のビルドには Git、Python 3、C++ ツールチェーン、Ninja が必要です。

生成済みの DLL を再利用する場合は、対応するビルド記録があるフォルダーを `-NativeSkiaDirectory` に指定してください。
版・対象 OS・ハッシュ・ビルド記録が一致しない場合は配布物を生成しません。

## 公開ソースから生成する場合

公開ソースからも、前述のコマンドで配布物を生成できます。
必要なツールとアナライザーの取得については、[開発環境とビルド](build.md#公開ソース-zip-からビルドする)を参照してください。

## 対応するソースを生成する

バイナリと同じコミットから、ソース ZIP とチェックサムを生成してください。
作業ツリーに未コミットの変更がある場合は生成できません。

~~~powershell
./installer/New-SourcePackage.ps1 -OutputDirectory 'C:/build/release' -ReleaseId 'v1.0.0' -Revision HEAD
~~~

ZIP にはコミットされたソースと `SOURCE-INFO.json` が入り、Git 履歴・未追跡ファイル・ローカル設定は入りません。
配布時には、そのバイナリに対応するソースとライセンス表記も提供してください。

## GitHub Release

ブランチは git-flow に沿って、次の手順で運用してください。

1. 機能変更は `develop` から `feature/<変更内容>` を作成し、PR で `develop` へ統合してください。
2. リリース時は `develop` から、同じリポジトリに `release/vx.y.z` または `release/vx.y.z-<接尾辞>` を作成してください。`Version.props` もリリースするバージョンに揃えてください。
3. リリースブランチを PR で `main` へ統合してください。[Release ワークフロー](../../.github/workflows/release.yml)がマージ後のコミットをビルド・試験し、ビルドと配布物の生成・保存が成功すると Windows インストーラー ZIP、対応ソース ZIP、それぞれのチェックサムを添付した Release を公開します。
4. Release の公開が成功すると、CI が公開対象の `main` のマージコミットを `develop` へ自動で取り込みます。取り込み結果は `Sync released main into develop` ジョブの Summary で確認してください。

たとえば、`release/v1.2.3` は正式版のタグ `v1.2.3`、`release/v1.2.3-rc.1` はプレリリースのタグ `v1.2.3-rc.1` になります。CI はブランチ名から `release/v` を除いたバージョンをビルドに使用します。タグは CI が作成するため、先に作成する必要はありません。

テストに失敗しても、ビルドと配布物の生成・整合性確認が成功していれば公開されます。テストの失敗は `Report test failures` ジョブの失敗として残り、Windows ジョブの Summary と `test-results` に結果が保存されます。Release の公開だけでは全試験の通過を意味しないため、導入前に結果を確認してください。ビルド・配布物の生成・保存に失敗した場合や、実行をキャンセルした場合は公開されません。

`develop` への自動取り込みも公開成功を条件とし、テストの成否とは分けて実行されます。公開したコミットだけが対象で、その後 `main` に加わった変更は含まれません。既に取り込み済みの場合は変更せずに完了します。

競合した場合は、CI が公開対象のコミットから `feature/sync-vx.y.z[-接尾辞]-to-develop` を作成し、`develop` への取り込み PR を開きます。Summary から PR を開き、取り込み用ブランチで競合を解消して **Create a merge commit** でマージしてください。再実行時は同じ版の PR を再利用し、手動で加えた修正を上書きしません。マージせず閉じた PR は自動で作り直さないため、取り込みを再開する場合はその PR を開き直してください。

PR の自動作成には、リポジトリの **Settings → Actions → General → Workflow permissions** で **Allow GitHub Actions to create and approve pull requests** を有効にしてください。組織ポリシーで禁止されている場合は、組織管理者による許可も必要です。ワークフローは競合解消用の PR を承認・マージしません。権限不足など、競合以外の原因で取り込みに失敗した場合はジョブのログを確認してください。公開済みの Release は維持されます。

Release 本文には、GitHubが生成する変更内容・該当する場合の初参加者・変更履歴へのリンクに続けて、`Build Results` と公開に使用したCI実行へのリンクを掲載します。テストの成否や個別のスキップは、リンク先のCI結果で確認してください。

### 公開せずにCIを手動実行する

[Validate Clutch-CS](../../.github/workflows/validate.yml) を手動実行すると、タグや GitHub Release を作成せずに検証できます。既定では、リリース時と同じビルド・試験・インストーラー検証を実行します。
GitHub の Actions で `Validate Clutch-CS` を開き、`Run workflow` から検証するブランチを選択して実行してください。GitHub CLI でも起動できます。

~~~powershell
gh workflow run validate.yml --ref feature/my-change
~~~

対象には、ワークフロー定義を含むブランチを指定してください。
一部の工程を省く場合は、`Run workflow` で該当するスキップ項目を選択してください。CLI では `-f <項目名>=true` を指定できます。

| 項目 | 省略する工程 |
| --- | --- |
| `skip_scripts` | リリース規則・バージョン管理・配布・画面スクリプトの試験 |
| `skip_unit` | 単体試験（性能試験を除く） |
| `skip_client` | Client試験（性能試験を除く） |
| `skip_installer` | Installerの通常試験 |
| `skip_integration` | 結合試験（性能試験を除く） |
| `skip_performance` | 全プロジェクトの性能試験 |
| `skip_package` | 配布物の生成、実インストール・更新・修復・削除の検証、生成物の保存 |

たとえば、通常の結合試験だけを実行する場合は、次のように指定してください。

~~~powershell
gh workflow run validate.yml --ref feature/my-change -f skip_scripts=true -f skip_unit=true -f skip_client=true -f skip_installer=true -f skip_performance=true -f skip_package=true
~~~

特定のクラスやメソッドに絞る場合は、次の項目に `dotnet test --filter` と同じ形式で条件を指定してください。対象工程のスキップ項目は `false` にしてください。

| 項目 | 絞り込む試験 |
| --- | --- |
| `unit_filter` | 通常の単体試験（性能試験を除く） |
| `integration_filter` | 通常の結合試験（性能試験を除く） |
| `performance_filter` | 全プロジェクトの性能試験 |

たとえば、`-f 'unit_filter=FullyQualifiedName~FileTransactionTests'` を追加すると、単体試験の対象を指定したクラスに絞れます。結合試験では `-f 'integration_filter=FullyQualifiedName~ScheduledUpdateProcessTests|FullyQualifiedName~InstallerTcpUpdateTests'`、性能試験では `-f 'performance_filter=FullyQualifiedName~WorkflowPerformanceTests'` のように指定できます。

空欄の場合は、その分類の試験をすべて実行します。単体・結合試験は条件に一致する試験が0件の場合に失敗します。性能試験は、全プロジェクトを通じて一致する試験が0件の場合に失敗します。

ビルドとアナライザーは毎回実行されます。スキップ・絞り込み指定は手動実行にのみ適用され、リリース時は全工程・全試験が実行されます。
選択した工程は実行結果の Summary で確認できます。試験・診断結果は `test-results`、配布物を生成した場合は `validation-windows` から取得できます。
テストに失敗しても後続の工程と結果の保存を続け、手動実行全体の結果は失敗になります。

### Windows ランナーを変更する

Windows ジョブは、リポジトリの Actions 変数 `RELEASE_WINDOWS_RUNNER` に指定したラベルのランナーを使用します。未設定時は `windows-latest` を使用します。

変更する場合は、組織の Actions 設定で `Windows Server 2025 with VS2026` イメージの Windows x64 ランナーを作成し、このリポジトリに利用を許可してください。そのラベルを `RELEASE_WINDOWS_RUNNER` に設定すると、次回から使用されます。ランナーには `global.json` に対応する .NET SDK が必要です。

## OS ごとの確認範囲

Windows は必須対応環境です。Linux・macOS は Best-Effort とし、配布に必要なネイティブビルド環境と、OS 固有機能の動作をそれぞれ確認してください。
対象 RID の指定が受け付けられることだけでは、その OS での導入・稼働確認が済んだことにはなりません。

## 第三者ライブラリの表記を更新する

依存版を更新した際は restore 後に `installer/Update-OssNotices.ps1` を実行し、生成された版・本文を確認してコミットしてください。既定の対象は Windows x64 です。別の配布先やランタイムを使う場合は、配布ビルドと同じ `-RuntimeIdentifier`、`-RuntimeVersion` を指定してください。

ライセンスの選択と補足通知は `installer/ThirdParty/packages.json` で管理し、インストーラーと Web クライアントに反映します。
