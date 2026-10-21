# サンプル一覧

[ドキュメント一覧](../docs/README.md) · [ワークフローの作成手順](../docs/guides/workflows.md)

試したい処理からサンプルを選んでください。
各フォルダーの README に、使用するファイルと変更が必要な設定を記載しています。
ノードの全設定と制約は[ノード一覧](../docs/reference/nodes/README.md)で確認できます。

| 試したい処理 | サンプル |
| --- | --- |
| 数値・条件・データの変換 | [データ処理](generic-data/README.md) |
| 配列を繰り返し処理する | [並列 ForEach](generic-iteration/README.md) |
| UDP の中継、HTTP の受付・応答 | [通信](generic-communication/README.md) |
| PC 間でファイルを転送する | [ファイル転送](generic-files/README.md) |
| アプリケーションと資源を監視する | [プロセス・リソース](generic-process-resources/README.md) |
| 音声生成、画像変換 | [AI・メディア](generic-ai-media/README.md) |
| C#・Python・JavaScript で文字列配列を整える | [スクリプト](scripting/README.md) |
| 管理 CLI に渡す要求データを用意する | [CLI の要求例](cli/README.md) |

## ワークフローの例を使う

1. 試すサンプルの README と定義ファイルを確認してください。
2. PC、ネットワーク、パス、接続先などを利用環境に合わせて変更してください。
3. WorkflowEditor へインポートし、検証結果を確認してください。
4. 保存・反映してから有効にし、実行結果を確認してください。

外部 API、機器、ファイル、プロセスを使うサンプルには、それぞれの準備が必要です。
CLI の要求例やスクリプトのコードは、ワークフロー定義とは形式が異なります。各 README の手順を使用してください。
