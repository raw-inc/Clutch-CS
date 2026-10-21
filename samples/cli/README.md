# CLI に渡す要求データの例

[サンプル一覧](../README.md) · [管理 CLI のコマンド](../../docs/reference/command-line.md)

このフォルダーの JSON は、管理 CLI の `--body` に渡す要求データです。
ワークフロー定義としてインポートするファイルではありません。

| ファイル | 用途 |
| --- | --- |
| [run.json](run.json) | ワークフローの実行 |
| [cancel.json](cancel.json) | 開始した処理の取消 |
| [stop.json](stop.json) | 待受ノードの停止 |
| [safety.json](safety.json) | 停止・継続に関する安全設定 |
| [capture.json](capture.json) | スナップショットの取得 |
| [restore.json](restore.json) | スナップショットからの復元 |
| [compatibility.json](compatibility.json) | 動作契約の互換性確認 |
| [log-query.json](log-query.json) | ログの検索 |

1. 実行する操作のファイルをコピーしてください。
2. 対象 ID や版などを、接続先から取得した値に変更してください。
3. ClientHost の URL、クラスター、ピアを明示してコマンドを実行してください。
4. 返された操作 ID を使って結果を確認してください。

コマンドの実行方法と、本文に指定する値は[管理 CLI](../../docs/reference/command-line.md)を参照してください。
