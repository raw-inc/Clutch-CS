# ソース構成と責務

Clutch-CS.slnx に含まれるプロジェクトの役割と、機能ごとの実装場所をまとめています。利用方法は[資料一覧](../README.md)、導入・起動は[インストール](../guides/install.md)を参照してください。

利用・開発で使う用語の定義は、[ドメイン用語集](glossary.md)を参照してください。

以下では、プロジェクト名の共通接頭辞 `ReArtWord.Contents.BackgroundService.` を省略して記載します。ソースの検索やビルドでは、実際のプロジェクト名・名前空間を使用してください。

## プロジェクトの境界

`src` のプロジェクトは、実行物・共有契約・実行基盤・OSや通信との接続・画面という境界を維持します。ディレクトリを細分化するためだけに、新しいプロジェクトやインターフェースを作りません。

依存欄は製品プロジェクトへの直接参照です。共通 SourceCodeQuality Core Analyzer は全C#プロジェクトで使用し、Blazor 部分には Razor Analyzer、Avalonia 部分には MVVM Analyzer を追加します。

| プロジェクト | 責務 | 直接依存 |
| --- | --- | --- |
| `Contracts` | 共有する型、定義、通信・保存の値、純粋な契約検証 | なし |
| `Runtime` | ノードの実行契約、ワークフローの検証・実行・待受、実行時の方針 | `Contracts` |
| `ManagementTransport` | UDP管理通信・受動観測、専用転送のクライアントとフレーム形式 | `Runtime` |
| `Nodes` | ワークフローで利用する標準ノードと、その処理に必要な実装 | `Runtime` |
| `Infrastructure` | クラスター参加・複製・永続保存、OS資源、共有設定、ログ、更新 | `Runtime`、`ManagementTransport` |
| `CoreService` | 常駐サービスの構成、管理操作の受付、CoreService所有の実処理と補助プロセス | `Infrastructure`、`Nodes` |
| `SignalCapture` | CoreServiceが起動するNativeAOTのパケット取得・解析プロセス。原本キューと解析状態をCoreServiceのヒープから分離し、標準入出力のバイナリpipeで中継 | なし |
| `Updater` | CoreService／ClientHostから独立した配置・復元・導入確認の実行物 | `Infrastructure` |
| `Client` | Blazorの画面・編集状態・ブラウザー境界 | `Contracts` |
| `ClientHost` | 画面配信、端末内HTTP窓口、発見したピアへの中継、全ピア更新の所有 | `Client`、`Infrastructure`、`ManagementTransport` |
| `Installer` | Avalonia の設定画面、対象確認、配置操作と既存更新処理への引き渡し | `Infrastructure` |
| `CommandLine` | ClientHostのHTTP窓口を使う管理CLI | なし |

`CoreService` は `Client`／Blazorを参照せず、GUIなしで動作します。`ClientHost` の観測はピア参加とは別であり、ブラウザーの接続先とワークフローの実行先を同一視しません。プロジェクトをまたぐ参照はcsprojに明示し、namespaceを分けただけで依存がなくなったとは扱いません。

## 配置と命名の規則

- C#のnamespaceは原則 `プロジェクトのRootNamespace + ディレクトリ` と一致させます。型は主な責務の所有先に一つだけ置きます。
- まず機能を表す既存の領域を選び、その領域が複数の責務を持つ場合に限り一段分けます。単純な型・通信・検証をすべて別々のフォルダーへ移すことはしません。
- 同じ責務には層をまたいでも `Snapshots`、`Transfers`、`Scheduling`、`Fleet`、`Logging`、`Tracing` など同じ名前を使います。小さくまとまった領域は浅いままで構いません。
- `Common`、`Helpers`、`Services` など広い名前を新しい置き場として増やさず、既存の所有先を先に確認します。複数の機能が共用する契約や調整役は親の領域に残します。
- `.razor`、`.razor.cs`、同じ型のpartialファイル、`.razor.css` は同じディレクトリに置きます。移動時にはRazorが生成するnamespaceとcode-behindを一致させ、利用する側の `@using` も更新します。
- namespaceと型名を衝突させません。たとえば `Dashboard` 型のページ配置は `Pages/Overview`、`RemoteTools` 型は `Pages/Tools` です。`WorkflowEditor` 名前空間内の画面型は `WorkflowEditorPage` とします。
- `Program.cs` は起動入口です。ホストの組立ては `Hosting/Composition`、継続するバックグラウンド処理は `Hosting/Workers`、個々の機能はそれぞれの領域に置きます。

OSネイティブAPIの宣言には `LibraryImport` を使用します。文字コード、BOOLの幅、関数名、DLL検索先、最終エラーの扱いを各境界で明示し、Win32のA/W接尾辞の自動補完には依存しません。UTF-16出力領域は呼出中だけ固定し、文字数とバイト数をAPIの契約に合わせます。固定長文字列を含むトレイ構造体は既存のUnicode配置を一時領域へ展開して渡し、登録名の解放後も元のWin32エラーを維持します。

unsafeはこの境界と生成スタブを持つプロジェクトに限定し、ポインターを扱う手書きメソッドには `Unsafe` 接尾辞を付けます。

## Razor の実装規約

画面の状態・取得・イベント処理は `.razor.cs` に置き、`.razor` には表示式、繰り返し、条件表示、イベントの接続を残してください。
大きいコンポーネントだけ、編集・取得・ブラウザー操作などの責務に応じて partial を分けてください。

共通の `ReArtWord.CSharp.SourceCodeQuality.Razor.Analyzers` が、Razor 内のメンバー宣言（`RAWRAZOR001`）と、コンポーネントからの CancellationToken 伝搬（`RAWRAZOR002`）を検査します。
検査規則とテストは[共通アナライザーのリポジトリ](https://github.com/raw-inc/ReArtWord.CSharp.SourceCodeQuality.Analyzers/tree/main/src/ReArtWord.CSharp.SourceCodeQuality.Razor.Analyzers)で管理します。取得と受入コミットの確認は[通常のビルド](build.md#通常の開発環境を用意する)に組み込まれています。

通常の画面 I/O は `LocalizedComponentBase.UiCancellation`、独自の監視処理はその画面が所有するトークンを使ってください。
画面終了時には購読を解除し、開始した処理をキャンセルして完了を待ってください。解放処理には、画面終了でキャンセル済みのトークンを渡さないでください。
JavaScript オブジェクトの取得が完了する前に画面を閉じた場合も、取得したオブジェクトが確実に破棄されるよう、取得と破棄の順序を制御してください。

Blazor が定めるコールバックの宣言や Parameter の setter など、変更できない宣言への診断を抑制する場合は、個別の `SuppressMessage` を使用してください。
直前の日本語コメントと `Justification` に、その理由を記載してください。

## 機能を探す入口

表のパスは `プロジェクト/ディレクトリ` です。たとえば `Runtime/Workflows` は `src/ReArtWord.Contents.BackgroundService.Runtime/Workflows` を表します。

| 機能 | 主な配置 |
| --- | --- |
| ワークフロー定義・型 | `Contracts/Workflow`、`Contracts/Nodes`、`Contracts/Values` |
| 共有設定・保存と反映 | `Contracts/Configuration/{Operational,Workflows,Editing,Detached}`、`Infrastructure/Configuration/{Loading,Operational,Workflows,Detached}` |
| スナップショット | `Contracts/Configuration/Snapshots`、`Infrastructure/Configuration/Snapshots`、`CoreService/Management/Snapshots`、`Client/Operations/Snapshots` |
| ワークフロー実行 | `Runtime/Workflows/{Compilation,Binding,Execution,Sources,Iteration,History,Retention,Targets}`。全体の入口は `Runtime/Workflows/WorkflowEngine.cs` |
| クラスター参加・要求・複製 | `Infrastructure/Cluster/{Membership,Requests,Replication,Synchronization,Conflicts}`。全体の入口は `Infrastructure/Cluster/PeerCluster.cs` |
| 永続保存 | `Infrastructure/Persistence/{Documents,Json,Sqlite,State,Receipts,Availability,History,Routing}`。共通repository契約は `Infrastructure/Persistence` |
| ログ・追跡・イベント | `Contracts/Observability/{Logging,Tracing}`、`Infrastructure/Observability/{Logging,Tracing,Events}` |
| 手動安全操作 | `Contracts/Execution/Safety`、`Infrastructure/Execution/Safety`、`Client/Operations/Execution` |
| ファイル操作・転送 | `Contracts/Files/{Browsing,Transfers,Transactions}`、`Runtime/Files/{Browsing,Transfers,Transactions}`、`Nodes/FileSystem/{Transfers,Snapshots}`、`CoreService/Files/Transfers`。通信の入口は `ManagementTransport/Files`、ブラウザー中継は `ClientHost/Files` |
| 更新 | `Contracts/Updates/{Client,Cluster,Fleet,Local,Releases,Scheduling,Streams}`、`Infrastructure/Updates` の同じ責務の下位領域、`ClientHost/Updates/Fleet`、`Client/Operations/Updates` |
| 導入・修復・削除 | `Contracts/Installation` の固定入力と結果、`Infrastructure/Installation` の配置・起動登録・所有確認、`CoreService/Management/SetupManagement` の遠隔受付、`Updater/SetupRunner` の独立実行。GUI の状態と表示は `Installer/ViewModels` と `Installer/Views` が所有 |
| RemoteDesktop | `Contracts/Desktop`、`ManagementTransport/Desktop`、`CoreService/Desktop/{Audio,Video,Capture,Input,Tray}`、`ClientHost/Desktop`。`Input`が接続ごとの未操作モードを適用し、`Tray`が対話ワーカー間の接続数・操作可能数の集約と通知なしのWindowsアイコン表示を所有 |
| Terminal | `Contracts/Terminal`、`ManagementTransport/Terminal`、`CoreService/Terminal`、`ClientHost/Terminal` |
| SignalScope | `SignalCapture`の取得・解析、`Contracts/SignalScope`、`ManagementTransport/SignalScope`、`CoreService/SignalScope`、`ClientHost/SignalScope`、`Client/wwwroot/signal-scope*`の表示・保持・検索 |
| HostManager | `Contracts/DeviceManagement/{Network,Hosts,Firewall,EnvironmentVariables,Display,Power,WindowsUpdate,Login}`、`Infrastructure/DeviceManagement`のOS境界とPC共通の無操作停止、`CoreService/Management/DeviceManagement.cs`、`CoreService/DeviceManagement`の対話ディスプレイ・入力連携、`ClientHost/DeviceManagement`の固定一括操作、`Client/DeviceManagement`と`Client/wwwroot/host-manager`の画面 |
| WorkflowEditor | `Client/WorkflowEditor/Pages`、`Client/WorkflowEditor/{Documents,Collaboration,Catalog,Graph,Fields,Navigation,Inspection,Diagnostics}`。型の入力欄は `Fields/Types` |
| Dashboard・運用操作 | `Client/Pages/Overview`、`Client/Overview/{Hosts,Telemetry,Charts,Updates}`、`Client/Operations/{Configuration,Execution,Journal,Persistence,Resources,Snapshots,Updates}` |
| 管理用の概況・発見・プレビュー | `Contracts/Management/{Overview,Discovery,Preview}`、`ClientHost/Management/Overview` |
| 標準ノード | `Nodes` の既存の機能領域。サブワークフローは `Control/Subworkflows`、反復は `Control/Iteration`、集合操作は `Data/Collections`、DBは `Database/{Relational,Redis}`、AI提供元別は `AI/{OpenAi,ElevenLabs}` |

同じ機能でも、DTOの定義、実行・通信、管理画面はそれぞれの層が所有します。たとえば全ピア更新の計画・実行・復帰確認は `ClientHost/Updates/Fleet`、その共有データは `Contracts/Updates/Fleet`、画面は `Client/Operations/Updates` と `Client/Overview/Updates` に分かれます。

`Client/wwwroot` のJavaScript・CSS・vendor資材は、既存の公開URLとWorker・Service Workerからの参照を維持します。C#/Razorのnamespace整理と、ブラウザーの配信URLの変更は別の変更です。ローカライズJSONとPython実行資材の埋込み名も維持します。

WorkflowEditorが所有する草稿・定義は編集時に新しいインスタンスへ置き換え、配下のコレクションをその場で変更しません。この所有範囲に限り、直列化済み草稿、内容識別子、表示順を除いた照合結果を弱参照で再利用します。外部入力や共有契約一般には可変オブジェクトのハッシュキャッシュを適用しません。ブラウザー保存の成功自体はキャッシュせず、IndexedDBの同じトランザクションで既存値を確認し、異なる場合だけ書き込みます。

保存領域の消失・別画面の変更・書込み不能を確認できる境界は維持します。

### ワークフロー接続の検査

一つの設定版を検査する間は、`WorkflowConnectionInspection` で設定値・型の推論・ポートの形状を共有します。配線ごとにグラフ全体の索引を作り直す処理を避けるためです。型の推論と再帰検出は独立して行い、設定の宣言範囲を維持します。別の編集版や並列処理では共有せず、保存・反映の検査ごとに作り直します。

## 変更時の互換性と確認

ソースの配置や名前空間を変更する際は、参照元と依存するプロジェクトを合わせて更新します。実行ファイル名、管理操作名、JSON のプロパティ、保存キー、設定形式を変える場合は、設定や保存済みデータへの影響も確認してください。

検証では、共通アナライザーを有効にして Release ビルドを実行してください。その後、単体テスト、Web クライアントとアナライザーのテスト、JavaScript のテスト、CoreService と ClientHost を実際に起動する結合試験を実施してください。Razor の配置を変更した場合は、ClientHost の発行時にも CSS・静的ファイル・配布物の整合を確認してください。ソースの文字列一致やファイル数を機能テストの代わりにしません。

テストは `tests` の機能別ディレクトリに置き、対象機能の正常時・失敗時の動作を確認します。共通アナライザーは `excludes` から参照し、製品ソースと分けて管理します。

## Terminal の実装とブラウザー試験

OS固有の収集処理はInfrastructureの`TerminalShellDiscovery`、起動時一覧の保持・要求の検証とPTY寿命はCoreServiceのTerminal、HTTP中継はClientHost、候補の選択と表示はGUIが担当します。GUIはCoreService実体ごとに取得結果を保持し、共有セッションの定期更新でShellを再収集しません。CoreService再起動による実体変更では新しい一覧を取得します。CoreService再起動時は共有セッションも終了します。

日本語IMEの確定文字は、後続キーや次の変換より先に一度だけ送ります。同じ文字を続けて確定する入力は省略しません。xterm.js 6.0.0には、確定前後の遅延処理が古いtextarea範囲を再送する問題があるため、配布資材にローカル修正を含めています。修正の再適用は `node installer/Patch-XtermIme.mjs` で行い、別版の資材はハッシュ不一致で停止します。

更新時は `terminal-ime.browser.cjs` をChromeとFirefoxで実行し、上流への更新でローカル修正を外せるか判断します。ブラウザー試験は `CONTENTS_TEST_PLAYWRIGHT` でPlaywrightの場所、`CONTENTS_TEST_BROWSER` で `chrome` または `firefox` を指定します。

ChromeのCDPによるIME経路と合成イベント順の検証は、OSのMicrosoft IMEの候補選択操作そのものとは区別します。
