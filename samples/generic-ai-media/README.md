# 音声生成・画像変換のサンプル

[サンプル一覧](../README.md) · [設定と動作の詳細](../../docs/reference/nodes/ai-media.md)

| ファイル | 内容 |
| --- | --- |
| [elevenlabs-speech.json](elevenlabs-speech.json) | ElevenLabs による音声生成 |
| [qwen-speech.json](qwen-speech.json) | Qwen による音声生成 |
| [picture-convert.yaml](picture-convert.yaml) | 画像形式の変換 |

## 使用する前に

音声生成では、実行先のピア ID、ネットワークアダプターの ID、ローカル IP、モデルと登録した秘密値の ID、利用する音声 ID を指定してください。例の `192.0.2.10` は、実行先 PC に割り当てられた IP アドレスへ変更してください。

画像変換では、実行先のピアに入力画像を用意してください。`picture-convert.yaml` は、`C:/Clutch-CS/content/source.png` を幅512ピクセルの WebP に変換します。「ファイル選択」のパスと「画像変換」の実行先・保存先を変更し、保存・反映後に「一回実行」を押してください。

保存先の例は `C:/Clutch-CS/content` です。生成・変換に使うパスを指定し、CoreService のファイル操作の許可範囲と、実行アカウントのアクセス権を確認してください。

ワークフローの読込み・保存・反映は[ワークフローを作る](../../docs/guides/workflows.md)の手順を使用してください。定義内の置換用の値を残したまま有効にしないでください。
