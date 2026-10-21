using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.ElevenLabs;

/// <summary>Eleven v4のText to Dialogue APIへ、一つの音声IDと読み上げ文を渡します。</summary>
internal sealed class ElevenLabsDialogueNode : AiOperationNode
{
    /// <summary>v3と同じ入出力を持ち、v4で対応する設定だけを公開します。</summary>
    internal ElevenLabsDialogueNode(ISecretStore secrets, AiOptions options, MediaOptions media) : base(secrets, options, media,
        "ai.audio.elevenlabs.v4.generate", Properties(), ["voice", "outputPath"], category: "AudioGeneration")
    {
    }

    /// <summary>ElevenLabs専用の固定認証ヘッダーを使います。</summary>
    protected override string? ApiKeyHeader => "xi-api-key";

    /// <summary>既存のElevenLabsノードと同じ音声IDの契約を検証します。</summary>
    /// <returns>設定スキーマと音声IDの問題です。</returns>
    protected override IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. base.ValidateSettings(definition)];
        string voiceKey = "voice";
        if (issues.Count == 0 && !ElevenLabsSpeechSettings.ValidVoice(new NodeSettings(definition.Settings).ReadText(voiceKey)!))
        {
            issues.Add(new("ai.audio.voice", "Use the ElevenLabs voice ID, containing letters, digits, hyphens or underscores.", definition.Id, "/voice"));
        }
        return issues;
    }

    /// <summary>公式の固定接続先またはローカル試験先を使用します。</summary>
    /// <returns>ElevenLabs APIの基点です。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings) => Options.ElevenLabsBaseUri();

    /// <summary>一回の非ストリーミング要求で音声を取得し、検証後に保存を確定します。</summary>
    /// <returns>MP3またはWAVのファイル情報です。</returns>
    protected override Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string prompt = Prompt(context);
        if (10000 < prompt.EnumerateRunes().Count())
        {
            throw new ArgumentException("Eleven v4 accepts at most 10,000 characters. The dialogue API recommends at most 2,000 for reliable generation.");
        }
        JsonObject body = new()
        {
            ["model_id"] = "eleven_v4",
            ["inputs"] = new JsonArray(new JsonObject { ["text"] = prompt, ["voice_id"] = settings.ReadText("voice") }),
        };
        AiRequestSettings.Copy(context.Settings, body, ("seed", "seed"), ("languageCode", "language_code"),
            ("textNormalization", "apply_text_normalization"), ("previousText", "previous_text"), ("nextText", "future_text"),
            ("previousRequestIds", "previous_request_ids"), ("nextRequestIds", "next_request_ids"),
            ("pronunciationDictionaries", "pronunciation_dictionary_locators"), ("usePvcAsIvc", "use_pvc_as_ivc"));
        JsonObject voice = [];
        AiRequestSettings.Copy(context.Settings, voice, ("stability", "stability"), ("similarity", "similarity"));
        if (voice.Count != 0)
        {
            body["settings"] = voice;
        }
        string format = settings.ReadText("outputFormat", "mp3_44100_128")!;
        string query = "?output_format=" + Uri.EscapeDataString(format);
        string loggingKey = "enableLogging";
        if (context.Settings.TryGetProperty(loggingKey, out JsonElement logging))
        {
            query += "&enable_logging=" + logging.GetRawText();
        }
        Uri endpoint = new(Options.ElevenLabsBaseUri(), "v1/text-to-dialogue" + query);
        string wavePrefix = "wav_";
        string mediaType = format.StartsWith(wavePrefix, StringComparison.Ordinal) ? "audio/wav"
                                                                                   : "audio/mpeg";
        return AiAudioOutput.GenerateAsync(context, session, Media, endpoint, body, mediaType, cancellationToken);
    }

    /// <summary>共通の保存形式を維持し、Dialogue固有の設定名と制約を定義します。</summary>
    /// <returns>安定度・類似度を含み、非対応の速度・スタイルを含まない設定です。</returns>
    private static string Properties()
    {
        JsonObject properties = JsonNode.Parse(ElevenLabsSpeechSettings.Properties(true))!.AsObject();
        properties.Remove("speed");
        properties["model"] = JsonNode.Parse("""{"type":"string","enum":["eleven_v4"],"default":"eleven_v4"}""");
        properties["similarity"] = JsonNode.Parse("""{"type":"number","minimum":0,"maximum":1}""");
        properties["usePvcAsIvc"] = new JsonObject { ["type"] = "boolean" };
        foreach (string key in new[] { "previousText", "nextText" })
        {
            properties[key] = JsonNode.Parse("""{"type":"string","maxLength":100}""");
        }
        foreach (string key in new[] { "previousRequestIds", "nextRequestIds" })
        {
            properties[key] = JsonNode.Parse("""{"type":"array","maxItems":3,"items":{"type":"string","minLength":1}}""");
        }
        return properties.ToJsonString();
    }
}
