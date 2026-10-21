using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI.ElevenLabs;

/// <summary>Eleven SoundEffectsの同期APIで効果音を生成します。</summary>
internal sealed class ElevenLabsSoundEffectsNode : AiOperationNode
{
    /// <summary>テキスト以外の音声生成パラメーターを任意として公開します。</summary>
    internal ElevenLabsSoundEffectsNode(ISecretStore secrets, AiOptions options, MediaOptions media) : base(secrets, options, media,
        "ai.audio.elevenlabs.soundEffects", Properties(), ["outputPath"], category: "AudioGeneration", modelRequired: false)
    {
    }

    /// <summary>BearerではないAPI専用認証です。</summary>
    protected override string? ApiKeyHeader => "xi-api-key";

    /// <summary>音声APIの接続基点を選びます。</summary>
    /// <returns>公式または試験先です。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings) => Options.ElevenLabsBaseUri();

    /// <summary>未指定の長さはサービスによる自動推定へ委ねます。</summary>
    /// <returns>完成したMP3生成物です。</returns>
    protected override Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        JsonObject body = new() { ["text"] = Prompt(context) };
        AiRequestSettings.Copy(context.Settings, body, ("model", "model_id"), ("durationSeconds", "duration_seconds"), ("promptInfluence", "prompt_influence"), ("loop", "loop"));
        string format = new NodeSettings(context.Settings).ReadText("outputFormat", "mp3_44100_128")!;
        return AiAudioOutput.GenerateAsync(context, session, Media, new Uri(Options.ElevenLabsBaseUri(), "v1/sound-generation?output_format=" + Uri.EscapeDataString(format)), body, "audio/mpeg", cancellationToken);
    }

    /// <summary>API仕様の秒数・強さの範囲を入力欄と検証で共有します。</summary>
    /// <returns>音声保存設定と効果音固有設定です。</returns>
    private static string Properties()
    {
        JsonObject properties = JsonNode.Parse(ElevenLabsSpeechSettings.AudioProperties)!.AsObject();
        properties["model"] = JsonNode.Parse("""{"type":"string","enum":["eleven_text_to_sound_v2"]}""");
        properties["durationSeconds"] = JsonNode.Parse("""{"type":"number","minimum":0.5,"maximum":30}""");
        properties["promptInfluence"] = JsonNode.Parse("""{"type":"number","minimum":0,"maximum":1}""");
        properties["loop"] = new JsonObject { ["type"] = "boolean" };
        return properties.ToJsonString();
    }
}
