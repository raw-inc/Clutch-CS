# Clutch-CS ドキュメント

**初めて使う場合は、下の 3 ページを順番に読んでください。**
設定値や内部の動作を詳しく調べたい場合は、後半のリファレンスから目的の項目を選べます。
クラスター、ピア、ノードなどの基本用語は、[README の用語](../README.md#用語)で確認できます。

## 初めて使う

| 順番 | ページ | ここでできること |
| --- | --- | --- |
| 1 | [インストール](guides/install.md) | 最初の PC に導入し、別の PC を同じクラスターに追加する |
| 2 | [管理画面を使う](guides/web-client.md) | 接続先を設定し、参加している PC と状態を確認する |
| 3 | [ワークフローを作る](guides/workflows.md) | ノードを配置してつなぎ、保存・反映・実行結果を確認する |

設定ファイルを用意して導入する場合は、[スクリプトによる導入](guides/manual-install.md)を使用してください。

## 利用ガイド

画面での操作を目的別にまとめています。英語の画面名・ボタン名は、操作する場所を見つけられるようにそのまま記載しています。

| やりたいこと | ページ |
| --- | --- |
| PC の稼働状態、設定の保存・反映状況を確認する | [クラスターを管理する](guides/cluster-management.md) |
| アプリケーションを登録し、起動・監視する | [アプリケーションを管理する](guides/applications.md) |
| IP、画面、電源、自動ログインなどを変更する | [PC の設定を変更する](guides/pc-management.md) |
| PC 間、または手元の PC とファイルをコピーする | [ファイルを転送する](guides/file-transfer.md) |
| 別の PC の画面を見て操作する | [画面を遠隔操作する](guides/remote-desktop.md) |
| 別の PC でコマンドを実行する | [ターミナルを使う](guides/terminal.md) |
| 通信内容を取得・検索する | [ネットワーク通信を調べる](guides/network-capture.md) |
| 処理が失敗した理由や過去の記録を調べる | [ログを調べる](guides/logs.md) |
| 更新、修復、アンインストールを行う | [保守の手順](guides/maintenance.md) |
| 接続できない、同期できない、更新が止まった | [トラブルシューティング](guides/troubleshooting.md) |

## 設定・仕様のリファレンス

操作の前提、設定値の意味、制限、失敗した場合の動作を確認するための資料です。

| 調べる内容 | ページ |
| --- | --- |
| 設定ファイル・保存先・通信ポートの対応 | [設定の場所](reference/configuration.md) |
| 編集操作、型、保存、実行先 | [ワークフローの編集仕様](reference/workflow-editor.md) |
| プレビュー、状態の保存、制御ノード | [プレビュー・状態・制御](reference/workflow-nodes.md) |
| 開始・完了信号、値の合流、待受 | [信号と実行](reference/workflow-runtime.md) |
| 通信、データ、ファイル、AI、スクリプトなどのノード | [ノード一覧](reference/nodes/README.md) |
| 表示名、タイムゾーン、Dashboard の集計・保持 | [管理画面の設定](reference/web-client.md) |
| アプリケーションの起動条件・自動復旧 | [アプリケーションの設定](reference/applications.md) |
| OS ごとの設定項目・権限・適用条件 | [PC 管理の設定](reference/pc-management.md) |
| ファイル転送の経路・上限・中断時の動作 | [ファイル転送の仕様](reference/file-transfer.md) |
| 画質・音声・入力・再接続 | [リモートデスクトップの設定](reference/remote-desktop.md) |
| 通信キャプチャの検索式・保持上限・必要な権限 | [通信キャプチャの仕様](reference/network-capture.md) |
| 保存・共有・反映、結果不明、管理通信 | [クラスターの動作](reference/cluster-operations.md) |
| ログ・履歴の保存期間、圧縮、同期 | [ログ・履歴の保存設定](reference/history-storage.md) |
| サービス登録と起動確認の条件 | [サービス導入スクリプト](reference/service-installation.md) |
| 診断コード・手動での復旧手順 | [手動復旧](reference/recovery.md) |
| 更新予約、API、失敗時の復帰 | [更新の仕様](reference/update-process.md) |
| コマンドと要求データの例 | [管理 CLI](reference/command-line.md) |

実際の設定ファイルやワークフロー例は[サンプル一覧](../samples/README.md)から探せます。

## 開発する

| 作業 | ページ |
| --- | --- |
| システムの用語と役割を調べる | [ドメイン用語集](development/glossary.md) |
| 開発環境を用意してビルドする | [開発環境とビルド](development/build.md) |
| 実装場所や責務を確認する | [ソース構成](development/architecture.md) |
| JavaScript・CSS の検査を行う | [JavaScript の静的解析](development/javascript.md)・[CSS の静的解析](development/css.md) |
| 負荷や障害時の動作を試験する | [性能・復旧試験](development/performance-tests.md) |
| バージョンを更新する | [バージョン管理](development/versioning.md) |
| 配布物と対応するソースを生成する | [配布物を作成する](development/distribution.md) |
| 製品の目的と実装が守る条件を確認する | [製品の目的と機能範囲](reference/product-scope.md)・[機能要件](reference/requirements.md) |
