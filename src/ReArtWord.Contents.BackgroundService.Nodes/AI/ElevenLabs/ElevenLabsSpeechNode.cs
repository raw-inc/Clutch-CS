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

/// <summary>ElevenLabsの指定モデルと音声IDでMP3またはWAVを生成・検証します。</summary>
internal sealed class ElevenLabsSpeechNode : AiOperationNode
{
    /// <summary>対応モデルを明示する独立した音声生成ノードを作ります。</summary>
    internal ElevenLabsSpeechNode(ISecretStore secrets, AiOptions options, MediaOptions media, bool v3 = false) : base(secrets, options, media,
        v3 ? "ai.audio.elevenlabs.v3.generate"
           : "ai.audio.elevenlabs.generate", ElevenLabsSpeechSettings.Properties(v3),
        ["voice", "outputPath"], category: "AudioGeneration", outputs: [new PortDescriptor("value")])
    {
    }

    /// <summary>ElevenLabs専用の固定認証ヘッダーを選びます。</summary>
    protected override string? ApiKeyHeader => "xi-api-key";

    /// <summary>音声IDを単一のURLパス要素として扱えることを検証します。</summary>
    /// <returns>設定スキーマと音声IDの問題です。</returns>
    protected override IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. base.ValidateSettings(definition)];
        if (issues.Count != 0)
        {
            return issues;
        }
        string voice = new NodeSettings(definition.Settings).ReadText("voice")!;
        if (!ElevenLabsSpeechSettings.ValidVoice(voice))
        {
            issues.Add(new ValidationIssue("ai.audio.voice", "Use the ElevenLabs voice ID, containing letters, digits, hyphens or underscores.", definition.Id, "/voice"));
        }
        return issues;
    }

    /// <summary>公式ElevenLabs APIまたは明示したローカル試験先を選びます。</summary>
    /// <returns>固定の合成API接続先です。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings) => Options.ElevenLabsBaseUri();

    /// <summary>公式の非ストリーミングAPIから指定形式を保存し、probe後に確定します。</summary>
    /// <returns>検証済み音声のPeer・パス・ハッシュと媒体情報です。</returns>
    protected override Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string model = settings.ReadText("model")!;
        int maximumCharacters = model switch
        {
            "eleven_v3" => 5000,
            _ => 10000,
        };
        string prompt = Prompt(context);
        if (maximumCharacters < prompt.EnumerateRunes().Count())
        {
            throw new ArgumentException("The speech text exceeds the selected ElevenLabs model's character limit.");
        }
        JsonObject body = new() { ["text"] = prompt, ["model_id"] = model };
        AiRequestSettings.Copy(context.Settings, body, ("seed", "seed"), ("languageCode", "language_code"),
            ("textNormalization", "apply_text_normalization"), ("previousText", "previous_text"), ("nextText", "next_text"),
            ("previousRequestIds", "previous_request_ids"), ("nextRequestIds", "next_request_ids"), ("pronunciationDictionaries", "pronunciation_dictionary_locators"));
        JsonObject voice = [];
        AiRequestSettings.Copy(context.Settings, voice, ("stability", "stability"), ("similarityBoost", "similarity_boost"), ("style", "style"), ("speakerBoost", "use_speaker_boost"), ("speed", "speed"));
        if (voice.Count != 0)
        {
            body["voice_settings"] = voice;
        }
        string format = settings.ReadText("outputFormat", "mp3_44100_128")!;
        string query = "?output_format=" + Uri.EscapeDataString(format);
        string loggingKey = "enableLogging";
        if (context.Settings.TryGetProperty(loggingKey, out JsonElement logging))
        {
            query += "&enable_logging=" + logging.GetRawText();
        }
        Uri uri = new(Options.ElevenLabsBaseUri(), "v1/text-to-speech/" + Uri.EscapeDataString(settings.ReadText("voice")!) + query);
        string wavePrefix = "wav_";
        string mediaType = format.StartsWith(wavePrefix, StringComparison.Ordinal) ? "audio/wav"
                                                                                   : "audio/mpeg";
        return AiAudioOutput.GenerateAsync(context, session, Media, uri, body, mediaType, cancellationToken);
    }
}
