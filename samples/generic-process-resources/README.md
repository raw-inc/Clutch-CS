# プロセス・リソース監視のサンプル

[サンプル一覧](../README.md) · [設定と動作の詳細](../../docs/reference/nodes/processes.md)

| ファイル | 内容 |
| --- | --- |
| [process-watch.yaml](process-watch.yaml) | 登録したアプリケーションの監視 |
| [resource-warning.yaml](resource-warning.yaml) | 資源の観測値を使う判定 |

## 使用する前に

対象のアプリケーションを登録し、`process-watch.yaml` の `processId` と実行先 PC を設定してください。保存・反映後に「一回実行」を押すと、登録したアプリケーションが起動し、状態の監視が始まります。

`resource-warning.yaml` の監視対象と判定値は、利用する PC に合わせて変更してください。

ワークフローの読込み・保存・反映は[ワークフローを作る](../../docs/guides/workflows.md)の手順を使用してください。定義内の置換用の値を残したまま有効にしないでください。
