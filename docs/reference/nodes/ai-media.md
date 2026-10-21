# AI・メディアノード

[ノード一覧](README.md) · [サンプル](../../../samples/generic-ai-media/README.md)

AI による生成・分類と、画像・音声・動画の変換に使うノードです。生成ノードは処理の完了を確認し、テキストの応答または完成したファイルを返します。利用できるモデルやサービスは、使用するアカウントの契約も確認してください。

## 現在の対応範囲

| ノード | 接続するAPIと入力 | 完了時の出力・制約 |
| --- | --- | --- |
| `ai.openai.chat` | OpenAI Chat Completions、`POST /v1/chat/completions`。文字列またはmessages配列 | 本文 `value` と応答JSON `response`。model・入力は必須 |
| `ai.openai.responses` | OpenAI Responses、`POST /v1/responses`。文字列またはinput配列 | 完了した本文 `value` と応答JSON `response`。保存prompt等による入力省略にも対応 |
| `ai.openai.chat.message` / `ai.openai.responses.message` | roleとcontentからメッセージ一覧を構築。外部通信なし | `messages` に前の一覧をつなぎ、`value` を次の組立てまたは生成へ渡す |
| `ai.audio.generate` | OpenAI Speech、`POST /v1/audio/speech`。読み上げ文字列と `voice` | WAV（既定）またはMP3。任意の `endpoint`・`instructions`・`speed` を指定可能 |
| `ai.audio.qwen.generate` | Qwen3-TTS-VC、`POST /api/v1/services/aigc/multimodal-generation/generation`。読み上げ文字列と登録済み音声ID | WAVファイル。対応モデルは `qwen3-tts-vc-2026-01-22`。リージョンはSingaporeまたはBeijing |
| `ai.audio.elevenlabs.generate` | ElevenLabs、`POST /v1/text-to-speech/{voice_id}`。読み上げ文字列と利用可能な音声ID | MP3またはWAV。モデルは `eleven_multilingual_v2`。任意の音声設定・速度・seed・正規化・前後文・発音辞書に対応 |
| `ai.audio.elevenlabs.v3.generate` | Eleven v3、`POST /v1/text-to-speech/{voice_id}` | MP3またはWAV。v3専用レシピ。安定度は0〜1の小数をそのまま送信 |
| `ai.audio.elevenlabs.v4.generate` | Eleven v4、`POST /v1/text-to-dialogue`。読み上げ文字列と音声IDを一組の `inputs` にして送信 | MP3またはWAV。安定度・類似度は0〜1の小数。速度・スタイル設定は非対応 |
| `ai.audio.elevenlabs.soundEffects` | Eleven SoundEffects、`POST /v1/sound-generation` | MP3。長さ（任意0.5～30秒）・プロンプト強度・ループ |
| `ai.audio.qwen.tts.generate` | Qwen3-TTS、DashScope形式。読み上げ文字列と標準音声名 | WAV。`qwen3-tts-flash`と対応スナップショット |
| `ai.audio.qwen.audio3.generate` | Qwen-Audio-3-TTS、`POST /api/v1/services/audio/tts/SpeechSynthesizer` | `qwen-audio-3.0-tts-flash`。WAV（既定24kHz）またはMP3。Workspace ID・リージョン・音声IDを指定 |
| `ai.destination.route` | `mediaKind` と `profiles` の設定。外部APIは呼び出さない | 元の `value` と選択したプロファイル名を、`text`・`audio`・`picture`・`video` の該当経路へ渡す |
| `ai.content.route` | OpenAI Chat Completions、`POST /v1/chat/completions`。テキスト、音声、画像、動画の抽出画像 | `classification` に分類名、`value` に元入力、`coverage` に対象媒体・抽出時点を返す |
| `media.audio.convert` | 許可されたローカルファイル | WAV・MP3・FLACへ変換 |
| `media.video.convert` | 許可されたローカルファイル | MP4・WebMへ変換 |
| `media.picture.convert` | 許可されたローカルファイル | PNG・JPEG・WebPへ変換 |

音声生成は、カタログの「音声生成」カテゴリからサービス別のノードを選択してください。`ai.audio.generate` はOpenAI専用です。OpenAIのモデル名は設定で必ず指定し、ノードが使用するAPIと入力形式に対応するモデルを指定してください。モデル名を自動で変更する機能はありません。

指示文・音量・ピッチ・Seedなどの任意API項目は、未指定またはJSON `null` の場合に送信を省略します。成功した入力のObjectに接続先プロパティがない場合も同じです。ForEachの要素に `feature` がない、または `"feature": null` の場合、Qwenの指示文へ接続したまま指示文なしで生成できます。保存欄の古い値へ戻したり、JSONスキーマの `default` を自動補完したりはしません。

`0`、`false`、空文字は指定値として型・範囲を検証します。モデル・Voice ID・保存先等の必須設定、条件付き必須設定、入力元の未到着・失敗・結果不明は省略として扱いません。

Eleven v4は、v3と同じ読み上げ文字列・Voice ID・保存先で利用できます。任意の安定度と類似度はDialogue APIの `settings` へ送り、未指定時はサービスの既定値を使用します。モデルの上限は10,000文字ですが、Text to Dialogue APIは安定動作のため2,000文字以内を推奨しています。前後文は各100文字以内です。長文の自動分割・再生成は行いません。

対応設定は [Eleven v4の説明](https://elevenlabs.io/docs/overview/capabilities/text-to-speech/eleven-v4) と [Text to Dialogue API](https://elevenlabs.io/docs/api-reference/text-to-dialogue/convert) に基づきます。

Qwen-Audio-3-TTSは既存のQwen3-TTSとは別のAPIを使います。Singapore（既定）／BeijingとWorkspace IDから専用URLを組み立て、互換サーバーを使う場合は完全なエンドポイントを指定します。速度・ピッチは0.5〜2、音量は0〜100、Seedは0〜65535です。言語の指定は一つの `language_hints` として渡します。SSMLは対応する音声で使用してください。

20,000文字を超える入力は送信前に拒否し、長文を暗黙に分割・再生成しません。接続方法は [国際向けHTTP利用例](https://www.alibabacloud.com/help/en/model-studio/non-realtime-tts-user-guide)、各設定は [HTTP APIリファレンス](https://help.aliyun.com/en/model-studio/qwen-audio-tts-http-api) に基づきます。

前者はSingapore、後者はBeijingを対象とするため、実行するアカウント・リージョンでモデルと音声の利用可否を確認してください。

実装の入口は [AIノードの登録](../../../src/ReArtWord.Contents.BackgroundService.Nodes/AI/AiNodeRegistration.cs) と [メディアノードの登録](../../../src/ReArtWord.Contents.BackgroundService.Nodes/Media/MediaNodeRegistration.cs) です。

### 対応していない処理

追加の生成サービス、音声認識・文字起こし、音声の登録とVoice ID取得、話者識別・音声特徴の抽出には対応していません。現時点で入力できるVoice IDは、外部サービス側で準備済みのものです。音声分類を文字起こしや話者識別として扱わないでください。

動画生成・画像生成のカテゴリにはレシピがありません。旧 `ai.text.generate` / `ai.picture.generate` / `ai.video.generate` は新規配置用のカタログに含まれません。保存済み定義の読込みと実行だけに互換実装を残しています。ノード取消によって外部サービス側の生成が停止したとは判断しません。

## 実行PC、ファイル、接続設定

実行PCの指定がなければ、クラスターの優先PCで実行します。ファイルがあるPCや特定のNICを使用する場合は、処理ノードの「実行PC」入力へPC選択ノードを接続してください。既存の明示ピア指定も使用できます。同じ入力について、二つの指定方法を併用することはできません。

生成ノードは、実行PCの許可範囲内で出力先の親ディレクトリを再帰作成します。`existingFileMode` は `overwrite`（既定）／`skip`、`lockedFileMode` は `error`（既定）／`skip` です。既存ファイルのスキップでは生成APIを呼びません。上書きでは完成媒体を一時ファイルで検証してから置き換え、失敗時には元の内容を保持します。メディア変換ノードは新規出力先を使用します。

音声・動画・画像を生成した成功結果は、`value` に次のファイル情報を返します。

```json
{"peerId":"実行したPeer","path":"完成ファイルのパス","mediaType":"媒体の種類","size":123,"sha256":"内容のハッシュ","probe":{}}
```

正常スキップでは `{"peerId":"実行したPeer","path":"既存パス","skipped":true,"skipReason":"existing"}` を返します。ロック時のスキップ理由は `locked` です。この参照に生成済み媒体の保証やハッシュは付けず、後続では同じピアの既存パスとして解決します。生成中に同名ファイルができた場合や保存直前にロックされた場合も同じ設定に従います。OS共有ロック以外の容量・権限エラーはスキップしません。

クラスターのファイル予約・結果不明の保護も引き続き適用されます。

ファイル情報には実行したピアを記録します。別PCの処理へ渡す場合は、先にファイル転送を行ってください。生成物と同じピアで実行する変換ノードへ、そのまま配線することもできます。大きな生成ファイルを `NodeValue` のバイナリとして返すことはありません。

生成・分類ノードには、`model`、`secretRef`、`interfaceId`、`localAddress` を設定します。`interfaceId` は実行PCのOSが付けたNIC識別子、`localAddress` はそのNICに割り当てられたIPv4アドレスです。既定NICへの切替、プロキシ、HTTPリダイレクトは使用しません。APIキーの実値をワークフローへ保存せず、登録済みの秘密情報を `secretRef` で参照します。

画面の「登録したAPIキーの名前」から **APIキーを選択・登録** を開き、登録済みの名前を選ぶか、名前と実際のAPIキーを入力して保存します。キーは選択中のクラスター内で共有されます。`secretRef` は環境変数やファイル名ではありません。同名のキーを更新した場合は、次に開始する処理から使用します。`localAddress` はAPIの接続先ではなく送信元です。実際の接続先URLはサービスとリージョンに応じて設定画面に表示されます。

出力ファイルのパスも、実行PCを指定して参照・手入力できます。

### 共通の上限と結果

| 項目 | 現在の設定・既定値 |
| --- | --- |
| AI処理全体の `timeoutMs` | 既定600,000ms。設定範囲1～86,400,000ms |
| 音声・動画の `downloadTimeoutMs` | 既定300,000ms。転送はこの上限と処理全体の残り時間の両方に従う |
| 音声の `maximumBytes` | 既定100MiB |
| 動画の `maximumBytes` | 既定1GiB |
| `maximumBytes` の設定可能な最大値 | 2,147,483,647バイト |
| APIのJSON応答 | 最大64MiB。画像のbase64もこの応答に含まれる |

| 発生したこと | 結果の扱い |
| --- | --- |
| 設定不備、秘密情報の不足、送信前のファイル・実行ファイルの不備 | `NotExecuted`。外部生成を開始しない |
| 送信前に利用者が取消 | `Cancelled` |
| 送信後、生成完了を確認できないまま通信切断・タイムアウト・取消 | `Unknown`。自動で再生成しない |
| APIが拒否・失敗を確定、または生成完了後の取得・形式確認・保存に失敗 | `Failed`。完成品として返さない |
| Wanのジョブ照会で取消済みを確認 | `Cancelled`。利用者によるノード取消とは区別する |
| 分類応答は完了したが、設定した分類名に一致しない | `Succeeded` の `indeterminate` 経路。通信の結果不明とは区別する |

結果不明でもWanのジョブIDを取得済みなら、`job.id` と `remoteCancellationConfirmed=false` を返します。同じIDを `jobId` 設定に指定すると、新しい生成要求を送らず、照会とファイル取得を再開します。この場合も通常の生成設定と `value` の文字列が必要です。API応答や例外に含まれる秘密情報は、そのままエラーやログへ出しません。

## テキスト生成

ChatとResponsesは別レシピです。`endpoint` は完全なURLを指定します。省略時はそれぞれOpenAIの `/v1/chat/completions` と `/v1/responses` を使用します。任意エンドポイントでも同じAPI形式を使います。文字列入力はChatでuserメッセージ一件に変換し、配列入力は構造を維持して渡します。

複数メッセージは「Chat メッセージを追加」または「Responses メッセージを追加」をつなぎ、前の `value` を次の `messages` へ接続します。画像等を含むcontent配列は、既存のJSON・オブジェクト・配列構築ノードでも作れます。

Chatの `model` と `value` は必須です。Responsesでは保存 `prompt` にモデルや本文を含められるので、これらを一律の必須項目にしません。`previousResponseId` と `conversation` は同時指定できません。温度・トークン上限・ツール・応答形式・保存指定等は、入力した項目だけ送信します。モデルによって対応オプションが異なります。

複数JSON型を許す設定欄ではJSONを入力してください（文字列の場合も引用符を含めます）。

`value` は本文、`response` はAPI応答JSONです。ツール呼出しのみの完了応答では本文が空になり、ツール要求は `response` から取り出せます。ツールの実行は自動で行いません。拒否、出力上限による途中終了、未完了応答は成功として扱いません。

仕様の参照: [Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[Responses](https://developers.openai.com/api/reference/cli/resources/responses/methods/create)。

## 音声生成

### Qwen3-TTS-VC

[ワークフロー例](../../../samples/generic-ai-media/qwen-speech.json)があります。`model` は `qwen3-tts-vc-2026-01-22` に固定です。`region` は `singapore`（既定）または `beijing`、`languageType` は `Auto`（既定）または `Japanese` などを指定します。APIキー、モデル、音声IDのリージョンをそろえてください。

```json
{
  "model": "qwen3-tts-vc-2026-01-22",
  "region": "singapore",
  "languageType": "Japanese",
  "voice": "登録済みの音声ID",
  "secretRef": "ai/qwen",
  "interfaceId": "実行PCのNIC識別子",
  "localAddress": "192.0.2.10",
  "outputPath": "C:/Clutch-CS/content/generated.wav"
}
```

`value` の読み上げ文字列は600文字以内です。Unicodeの符号位置で数えるため、サロゲートペアを使う絵文字は一文字として数えます。`finish_reason=stop` を確認し、`audio.url` から完成WAVを取得します。取得先はAlibabaの配布先、または指定エンドポイントと同じoriginに限定し、APIキーを転送しません。HTTP表記のURLはHTTPSへ変更して取得します。

音声の新規登録やリアルタイムのWebSocket通信は、このノードでは行いません。

`endpoint` に完全なDashScope API URLを指定すると、`region` の設定欄は無効となり実行時にも参照しません。任意のパスとクエリをそのまま使います。通常のQwen3-TTSも同じ形式で、標準音声名（例: `Cherry`）を使用します。

### OpenAI Speech

`value` に4,096文字以内の読み上げテキストを渡します。`model` と `voice` は必須です。`endpoint` 省略時はOpenAIの `/v1/audio/speech` へ接続します。`speed` は0.25～4、`instructions` は音声表現の指示です。`tts-1` と `tts-1-hd` は `instructions` に対応しません。`outputFormat` は `wav`（既定）か `mp3`。

認証不要の互換エンドポイントでは `secretRef` を省略できます。

### ElevenLabs

[ワークフロー例](../../../samples/generic-ai-media/elevenlabs-speech.json)があります。`voice` には、指定したAPIキーで利用できる音声IDを設定します。認証には `xi-api-key` ヘッダーを使い、既定では44.1kHz・128kbpsのMP3を取得します。`outputFormat` でMP3またはWAVを選べます。

WAVは `wav_8000`、`wav_16000`、`wav_22050`、`wav_24000`、`wav_32000`、`wav_44100`、`wav_48000` に対応します。44.1kHzのWAVはAPI側でPro以上のプランが必要です。`outputPath` は選択した形式に合わせ、新規の `.mp3` または `.wav` ファイルを指定してください。

実装が受け付ける文字数は、`eleven_multilingual_v2` が10,000文字、`eleven_v3` が5,000文字までです。Qwenと同じくUnicodeの符号位置で数えます。上限を超える入力は送信前に拒否し、自動分割しません。本文を最後まで受信した後に、選択したMP3／WAVの形式と音声ストリームを確認します。WAVを選ぶとAPIから直接WAVを取得し、再圧縮せず保存します。

v2とv3は別のレシピです。`stability`・`speed`・`seed`・`textNormalization`・`enableLogging`・発音辞書は任意で、未指定項目をAPIへ送りません。両モデルの `stability` は0〜1の小数を受け付け、例えば `0.35` を丸めずAPIへ送ります。モデル・音声・契約によるAPI側の拒否は実行失敗として返し、別の値への置換や自動再生成はしません。

v2は `similarityBoost`・`style`・`speakerBoost` と前後文・要求IDを指定できます。v3は `languageCode` を指定できます。

Eleven SoundEffectsは説明文を `value` に渡します。`durationSeconds` を省略するとAPIが長さを決めます。`promptInfluence` は0～1、`loop` はシームレスループの指定です。音声IDは不要です。

API契約の参照: [Eleven TTS](https://elevenlabs.io/docs/api-reference/text-to-speech/convert)、[Eleven SoundEffects](https://elevenlabs.io/docs/api-reference/text-to-sound-effects/convert)、[Qwen TTS](https://www.alibabacloud.com/help/en/model-studio/qwen-tts-api)、[OpenAI Speech](https://developers.openai.com/api/reference/cli/resources/audio/subresources/speech/methods/create)。

## 処理先の選択と内容分類

`ai.destination.route` は設定に基づいて経路を選びます。接続先サービスや認証設定は変更しないため、各経路の先へ設定済みの生成ノードを接続します。

```json
{"mediaKind":"audio","profiles":{"text":"text-profile","audio":"speech-profile","picture":"image-profile","video":"wan-profile"}}
```

`ai.content.route` はAIへ内容を送って分類します。`categories` には重複しない分類名を1～32個指定します。各分類名は64文字以内です。

| `mediaKind` | 入力・追加設定 | 分類対象 |
| --- | --- | --- |
| `text` | `value` に文字列 | 入力したテキスト |
| `audio` | バイナリ、許可されたパス、同じピアのファイル情報。`inputFormat` は `wav` または `mp3` | 最大32MiBの音声 |
| `picture` | バイナリ、許可されたパス、同じピアのファイル情報。`inputFormat` は `png`・`jpeg`・`webp` | 最大32MiBの画像 |
| `video` | 許可されたパスまたは同じピアのファイル情報。`frameTimesSeconds` に1～16個の時点 | 指定時点から幅512pxの画像を抽出して分類。音声は送らず、動画全体を理解する機能ではない |

動画の抽出時点は0～86,400秒で指定します。存在しない時点から画像を取得できなければ、分類APIを呼び出しません。抽出画像にも一枚32MiBの読取上限があります。`coverage.frameTimesSeconds` で分類した時点を確認できます。

`classification` を `control.condition` の `comparison` 入力、元の `value` を `value` 入力へ接続し、`expected` に分類名を設定すると、内容に応じて後続処理を分けられます。`indeterminate` 経路も接続して、分類できなかった場合の処理を用意してください。

## メディア変換

入力は許可されたパスの文字列、または同じピアのファイル情報です。`outputPath` と `format` を指定します。`codec` の省略時は、下表の各形式の先頭を使用します。

| ノード | 出力形式とコーデック | 任意設定 |
| --- | --- | --- |
| `media.audio.convert` | WAV: `pcm_s16le` / `pcm_s24le` / `pcm_f32le`、MP3: `libmp3lame`、FLAC: `flac` | `sampleRate`: 8,000～192,000（既定48,000）、`channels`: 1～8（既定2） |
| `media.video.convert` | MP4: `libx264` / `libx265`、WebM: `libvpx-vp9` / `libaom-av1` | `width`・`height`: 2～8,192、`frameRate`: 1～120 |
| `media.picture.convert` | PNG: `png`、JPEG: `mjpeg`、WebP: `libwebp` | `width`・`height`: 2～8,192 |

入力として許可する形式はWAV、MP3、FLAC、Ogg、MP4/MOV、Matroska/WebM、AVI、PNG、JPEG、WebPです。プレイリスト、ネットワーク上の入力、自由記述のフィルターは受け付けません。実際に利用できるコーデックは、指定したffmpegに依存します。`timeoutMs` は既定300,000msです。

タイムアウトや取消時は、開始した子プロセスとその子孫を停止し、終了を確認します。確認済みの利用者取消は `Cancelled`、停止を確認できなければ `Unknown` です。一時出力を完成品として返しません。

## 登録と実行ファイル

通常のCoreServiceにはノードが登録済みです。TOMLで実行物とファイルの許可範囲を指定します。既存設定では同じセクションへ追記してください。

```toml
[media]
ffmpegPath = "C:/opt/ffmpeg/bin/ffmpeg.exe"
ffprobePath = "C:/opt/ffmpeg/bin/ffprobe.exe"

[files]
roots = ["C:/Clutch-CS/content"]
```

実行物を省略するとCoreServiceの起動環境のPATHから `ffmpeg`／`ffprobe` を解決します。配布一式には含まれないため対象PCへ別途用意します。`[files]` の許可範囲はファイル操作とも共通です。秘密値は画面または[CLIの `secret set`](../command-line.md)で登録し、ワークフローには名前だけを指定します。

以下は独自ホストへ組み込む場合の登録例です。

```csharp
var media = new MediaOptions(
    "C:/opt/ffmpeg/bin/ffmpeg.exe",
    "C:/opt/ffmpeg/bin/ffprobe.exe",
    new FileSystemOptions(["C:/Clutch-CS/content"]));
IReadOnlyList<INode> mediaNodes = MediaNodeRegistration.Create(media);
IReadOnlyList<INode> aiNodes = AiNodeRegistration.Create(secretStore, new AiOptions(), media);
```

`MediaOptions` と `MediaNodeRegistration` は `Nodes.Media`、AIの登録と設定は `Nodes.AI` にあります。`FileSystemOptions` は `Contracts.Files`、`INode` は `Runtime.Nodes`、秘密情報の参照先は `Runtime.Secrets.ISecretStore` です。パスは導入先に合わせて変更してください。

## 検証できた範囲

Windowsの実ffmpeg/ffprobeを使い、音声・動画・画像の変換、形式とハッシュの照合、壊れた入力の拒否、既存ファイルの保護、変換中の取消を試験しています。

外部AIの試験は、ローカルのHTTP試験サーバーを使います。要求先・認証・JSON本文、生成完了判定、音声ファイルの取得、画像応答、Wanの定期照会・ジョブ再取得・取消時のID保持、四媒体の分類、文字数・転送サイズの上限、リダイレクト拒否を確認します。これらは外部サービスに実接続した試験ではありません。

有料APIへの実接続、課金、音声登録、モデルの出力品質、地域別の契約条件、他OSの実行結果は未確認です。これらの確認には、利用するアカウントと実機が必要です。

試験用実行ファイルは `CONTENTS_TEST_FFMPEG` と `CONTENTS_TEST_FFPROBE` で指定できます。試験サーバーは `AiOptions.LoopbackFixtureBaseUri` にHTTPループバックURLを指定します。本番ではこの値を設定しません。
