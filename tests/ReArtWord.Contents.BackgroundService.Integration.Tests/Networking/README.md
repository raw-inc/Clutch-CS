# 2 NIC の手動疎通確認

[関連する設定・開発資料](../../../docs/development/performance-tests.md)

二つのネットワークアダプターを使い、発見・参加・保存・実行の疎通を確認する手動試験です。コマンドはリポジトリのルートから実行してください。

`check_nics.py` は、同じ開発PCに割り当て済みのNIC／IPv4を明示して、試験専用の同一クラスターIDを使うCoreService 2台とClientHost 2台を起動します。通常モードでは両CoreServiceが一つのクラスターへ参加し、分離モードでは相互発見しないことを確認します。Python 3.10以降の標準ライブラリだけを使用します。ClientHostが既存UDP オブザーバーを所有し、試験スクリプトは公開ローカルHTTP APIを呼びます。UDPプロトコルの別実装はありません。

実行前に NIC / IP / prefix / FW を利用者が設定してください。試験スクリプトは IP、NIC、経路、FW を変更せず、別インターフェース / IP / ポートへ代替しません。同じクラスターの UDP ポートは A/B 共通です。通常モードはブロードキャストが別々の場合も相互到達を要求します。サブネット間で発見されないことを確認する場合は、下記の分離確認を選択してください。

## 実行

CoreService と ClientHost の apphost（または DLL）を、必要資材を含む発行ディレクトリから指定してください。それぞれのディレクトリ一式を新しい試験用ディレクトリへコピーするので、ビルド出力や既存状態を実行中に使用しません。

DLL を指定する場合は `dotnet` が PATH 上に必要です。

```powershell
python tests/ReArtWord.Contents.BackgroundService.Integration.Tests/Networking/check_nics.py `
  --core 'C:/build/published/Core/Clutch-CS.CoreService.exe' `
  --client-host 'C:/build/published/ClientHost/Clutch-CS.ClientHost.exe' `
  --a-interface '{NIC-A-ID}' --a-ip 192.168.10.101 --a-broadcast 192.168.10.255 --a-port 47740 `
  --b-interface '{NIC-B-ID}' --b-ip 192.168.10.102 --b-broadcast 192.168.10.255 --b-port 47740
```

通常モードは異なる NIC ID と IP を必須にします。試験スクリプト自身の動作確認だけを同じ NIC / IP で行う場合は、A/B に同じ明示値を渡して `--allow-same-nic-smoke` を追加してください。この場合、結果は常に `same-nic-smoke` であり、複数 NIC 確認済みとは扱いません。

追加指定は `--timeout 60`（各読取待ちの秒数、1～600）と `--output-root <試験結果の親ディレクトリ>` です。既定の親は実行ディレクトリの `.work/manual-network-checks` です。HTTP は空きループバックポートを各 ClientHost に割り当て、UDP は引数のポートを固定します。指定 IP が未割当、NIC が不一致、実 CoreService/オブザーバーが指定ポートで待受できない場合は失敗を記録します。既存アプリケーションは停止しません。

分離確認は異なる NIC・IP・ブロードキャストを指定し、`--expect-isolated --timeout 10` を追加してください。同じクラスター ID と UDP ポートの CoreService を両方へ起動し、指定秒数の間、各オブザーバーが自分側の CoreService を実際に発見し、反対側を発見しないことを確認します。両側とも何も受信しない状態を成功にせず、保存・実行要求は送りません。結果モードは `same-pc-subnet-isolation` です。例えば `192.168.10.0/24` と `192.168.20.0/24` に分ける場合は、各 NIC の IP と、ブロードキャスト先 `192.168.10.255`・`192.168.20.255` を指定してください。

## 確認と結果

- 両 ClientHost が指定 NIC / IP / ブロードキャストで監視できる。
- 両側から同じ独立クラスターの CoreService 2 台を発見する。
- 各 CoreService の参加数は 2 で、オブザーバー / ClientHost を含まない。
- 両 Web クライアントから両 CoreService の読み取り専用管理照会ができ、稼働確認の PID・実体 ID と観測 IP / UDP ポートが指定した実体に一致する。
- A からの保存を B で確認し、A 受付で両 CoreService の `control.pass` が `42` を返す。B からも更新保存と相互実行を行う。
- 反対側の CoreService から保存済み実行結果を確認する。

毎回重複しないクラスター ID と空の保存領域を作ります。操作先はそのクラスターの `nic-a` / `nic-b` だけです。書込みは一度だけ送り、不明応答に元 command ID があれば同じ結果を読取照会します。新しい ID による暗黙再実行はしません。

発見一覧は過去のハートビートの観測なので、直後の現在照会で CoreService が参加確認の同期中に移っている場合があります。その場合に限り、同じ PID・実体 ID の稼働確認を期限まで読取照会します。初期化・同期の問題、別実体への変更、同期以外の非 Ready 状態、期限切れは失敗として記録します。稼働確認の状態遷移と、両オブザーバーが見たクラスターの内容も結果に残します。

成功時終了コードは `0`、失敗時は `1`。`result.json` にモード、明示 binding、対象・段階、直近発見状態、元操作 ID、結果と cleanup を記録します。stdout/stderr と TOML、コピーした実行物も同じディレクトリに残します。失敗・Ctrl+C の場合も、この起動で所有したプロセスだけを終了します。

同一 PC の複数 NIC による擬似検証です。成功しても別物理 PC の FW・配線・スイッチ・到達性を確認したことにはなりません。

一つのClientHostで複数の独立クラスターを観測する機能は `MultiNetworkClientTests` で扱います。異なるUDPポートのループバックネットワークと実CoreService／ClientHostを使い、経路別の読取・書込・元操作記録の照会、ClientHost再起動後の監視復元を確認します。これは上記試験スクリプトによる実NICの疎通確認とは範囲が異なります。
