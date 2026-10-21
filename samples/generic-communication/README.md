# 通信のサンプル

[サンプル一覧](../README.md) · [設定と動作の詳細](../../docs/reference/nodes/communication.md)

| ファイル | 内容 |
| --- | --- |
| [udp-forward.json](udp-forward.json) | 受信した UDP データを別の宛先へ転送 |
| [http-workflow-echo.json](http-workflow-echo.json) | HTTP の要求を受け、ワークフローで応答 |
| [http-typed-input.json](http-typed-input.json) | HTTP の入力を型付きで受け取る |
| [optional-http-status.csx](optional-http-status.csx) | C# の省略可能な引数を使うコード例 |

## 使用する前に

ピア ID、ネットワークアダプターの ID、ローカル IP、ポート、送信先を変更してください。ローカル IP には、実行先 PC 自身に割り当てられた値を指定してください。C# ファイルはスクリプトノードのコード欄に使用します。

ワークフローの読込み・保存・反映は[ワークフローを作る](../../docs/guides/workflows.md)の手順を使用してください。定義内の置換用の値を残したまま有効にしないでください。
