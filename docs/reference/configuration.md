# 設定の場所

[ドキュメント一覧](../README.md)

変更したい内容によって、設定する場所と反映される範囲が異なります。
CoreService の起動設定、クラスターで共有する設定、ブラウザーだけの表示設定を区別してください。

## 設定する場所を選ぶ

| 変更したい内容 | 設定する場所 | 範囲・反映 |
| --- | --- | --- |
| クラスター・ピア・ホストの ID、NIC、IP、UDP ポート | インストーラー、CoreService の TOML | ピアごとの起動設定 |
| データ・ログ・更新ファイルの保存先 | CoreService の TOML | ピアごとの保存先 |
| ワークフローと共通の運用設定 | 管理画面または CLI | クラスターで共有。保存と反映を個別に確認 |
| 管理画面が観測するネットワーク | Select connection、ClientHost の `networks.json` | ClientHost ごと |
| 管理画面の待受 URL | インストーラー、ClientHost の `--listen` | ClientHost ごと |
| 管理画面のデータ保存先 | ClientHost の `--state` | 起動するユーザー・サービスごと |
| ヘッダーのタイトル・画像 | ClientHost の `branding.json` | ClientHost ごと |
| 言語・表示用タイムゾーン | 管理画面の表示設定 | ブラウザーごと |

既定の ClientHost 保存先は `%LOCALAPPDATA%/ReArtWord/Contents/Client` です。
通常ユーザーでの起動と、PC 共通のサービス起動では、保存先と権限が異なります。

## CoreService の TOML

設定例を含む手順は[スクリプトによる導入](../guides/manual-install.md)を参照してください。

| セクション | 主な設定 | 詳細 |
| --- | --- | --- |
| `[cluster]` | ID、ネットワーク、初回作成 | [初期設定](../guides/manual-install.md#2-各pcへ配布物と設定を置く) |
| `[storage]` | 保存先、SQLite / JSON、用途別の保存方式 | [保存先の設定](cluster-operations.md#用途別保存と保存不能時の扱い) |
| `[logs]` | ログ保存先、転送用 NIC・ポート、保持 | [ログ・履歴](history-storage.md) |
| `[updates]` | 配置先、一時領域、バックアップ、Updater | [更新の仕様](update-process.md#配置) |
| `[transferQueue]` | 転送受付、追加ネットワーク | [ファイル転送](file-transfer.md#転送先coreserviceが別のネットワークも使う場合) |
| `[time]` | 稼働用タイムゾーンの初期値 | [共通設定の扱い](cluster-operations.md#保存共有反映) |

TOML の共有設定は、新しいクラスターを作成する際の初期値に使われます。
通常の再起動では、保存済みの共有設定を TOML の値で上書きしません。
`initialize = true` は空の保存領域から新しいクラスターを作る最初の 1 台にだけ指定してください。

## 通信ポート

| 用途 | 既定・割当 |
| --- | --- |
| 発見・生存通知・管理通信 | UDP 47740 |
| ログ転送 | TCP 47741 |
| 管理・同期・更新・ファイル転送・遠隔操作 | CoreService が確保する TCP ポート |
| 管理画面 | HTTP `127.0.0.1:5081` |

ログ用の TCP 47741 だけを許可しても、ほかの TCP 経路は使用できません。
CoreService の実行ファイルに対する通信許可を、使用するネットワークに合わせて設定してください。
外部機器や API と通信するノードのポートは、そのノードの設定に従います。

GUI のセットアップでは必要な Firewall ルールを登録します。
手動で導入する場合の設定例は[スクリプトによる導入](../guides/manual-install.md#3-使用するlanの通信を許可する)に記載しています。
