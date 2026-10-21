using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>Qwen-Audio-3.0のHTTP合成APIで完成音声を取得します。</summary>
internal sealed class QwenAudioSpeechNode : AiOperationNode
{
    private static string NodeType => "ai.audio.qwen.audio3.generate";
    private static string ApiPath => "api/v1/services/audio/tts/SpeechSynthesizer";

    /// <summary>既存Qwen3-TTSとは異なるAPI契約を独立したノードとして公開します。</summary>
    internal QwenAudioSpeechNode(ISecretStore secrets, AiOptions options, MediaOptions media) : base(secrets, options, media,
        NodeType, Properties, ["voice", "outputPath"], category: "AudioGeneration", allowAnonymousEndpoint: true)
    {
    }

    /// <summary>完全エンドポイントを指定しない場合は、公式のWorkspaceとリージョンを要求します。</summary>
    /// <returns>外部要求前に分かる接続設定の問題です。</returns>
    protected override IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. base.ValidateSettings(definition)];
        NodeSettings settings = new(definition.Settings);
        string? endpoint = settings.ReadText("endpoint");
        string region = settings.ReadText("region", "singapore")!;
        string? workspace = settings.ReadText("workspaceId");
        string provider = ".aliyuncs.com";
        if (string.IsNullOrWhiteSpace(endpoint) && AiServiceConnection.Endpoint(NodeType, region, workspace) is null)
        {
            issues.Add(new("ai.qwen.workspace", "Specify the Workspace ID and the matching API region.", definition.Id, "/workspaceId"));
        }
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && uri.Host.EndsWith(provider, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.AbsolutePath.Trim('/'), ApiPath, StringComparison.Ordinal))
        {
            issues.Add(new("ai.qwen.audioEndpoint", "Use the complete Qwen-Audio SpeechSynthesizer endpoint.", definition.Id, "/endpoint"));
        }
        return issues;
    }

    /// <summary>Workspace専用の公式URL、または明示された互換APIを選びます。</summary>
    /// <returns>音声合成APIの完全URLです。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings)
    {
        string? endpoint = settings.ReadText("endpoint");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return AiRequestSettings.Endpoint(settings, Options.BaseUri(true));
        }
        Uri root = Options.LoopbackFixtureBaseUri is not null ? Options.BaseUri(true)
                                                              : AiServiceConnection.Endpoint(NodeType, settings.ReadText("region", "singapore")!, settings.ReadText("workspaceId"))!;
        return new(root, ApiPath);
    }

    /// <summary>停止理由と生成URLを確認し、認証を転送せず音声を保存します。</summary>
    /// <returns>検証済みWAVまたはMP3です。</returns>
    protected override async Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string prompt = Prompt(context);
        if (20000 < prompt.EnumerateRunes().Count())
        {
            throw new ArgumentException("Qwen-Audio-TTS accepts at most 20,000 characters per call.");
        }
        string format = settings.ReadText("outputFormat", "wav")!;
        JsonObject input = new() { ["text"] = prompt, ["voice"] = settings.ReadText("voice"), ["format"] = format, ["sample_rate"] = settings.ReadInt32("sampleRate", 24000) };
        AiRequestSettings.Copy(context.Settings, input, ("volume", "volume"), ("speed", "rate"), ("pitch", "pitch"),
            ("seed", "seed"), ("instructions", "instruction"), ("enableSsml", "enable_ssml"));
        string? language = settings.ReadText("languageCode");
        if (!string.IsNullOrEmpty(language))
        {
            input["language_hints"] = new JsonArray(language);
        }
        Uri endpoint = ProviderBaseUri(settings);
        JsonElement response = await session.JsonAsync(endpoint.AbsoluteUri, new JsonObject { ["model"] = "qwen-audio-3.0-tts-flash", ["input"] = input }, cancellationToken: cancellationToken).ConfigureAwait(false);
        string codeKey = "code";
        if (response.TryGetProperty(codeKey, out JsonElement code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString()))
        {
            session.TerminalKnown = true;
            throw new InvalidDataException("Qwen-Audio returned a provider error.");
        }
        JsonElement output = response.GetProperty("output");
        string stopped = "stop";
        string? finish = output.GetProperty("finish_reason").GetString();
        if (!string.Equals(finish, stopped, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Qwen-Audio completion is not confirmed.");
        }
        session.TerminalKnown = true;
        Uri download = new(output.GetProperty("audio").GetProperty("url").GetString()!);
        bool customOrigin = !string.IsNullOrWhiteSpace(settings.ReadText("endpoint")) && download.IsAbsoluteUri
            && string.Equals(download.GetLeftPart(UriPartial.Authority), endpoint.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
            && string.IsNullOrEmpty(download.UserInfo) && string.IsNullOrEmpty(download.Fragment);
        if (!customOrigin)
        {
            download = AiMediaOutput.AlibabaDownloadUri(Options, download, upgradeHttp: true);
        }
        string destination = settings.ReadText("outputPath")!;
        string temporary = MediaArtifact.TemporaryPath(Media, destination, allowExisting: true);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(settings.ReadInt32("downloadTimeoutMs", 300000)));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            _ = await session.DownloadAsync(download, null, temporary, new FileAccessPolicy(Media.Files), settings.ReadInt32("maximumBytes", 104857600), false, linked.Token).ConfigureAwait(false);
            string mediaType = format switch { "mp3" => "audio/mpeg", _ => "audio/wav" };
            return await MediaOutputFile.CommitAsync(Media, context, temporary, destination, mediaType, "audio", cancellationToken: linked.Token).ConfigureAwait(false);
        }
        finally
        {
            MediaArtifact.Cleanup(Media, temporary);
        }
    }

    /// <summary>HTTPで対応する項目だけを公開し、ストリーミング専用機能は混ぜません。</summary>
    private static string Properties => """
        {"model":{"type":"string","enum":["qwen-audio-3.0-tts-flash"],"default":"qwen-audio-3.0-tts-flash"},
         "voice":{"type":"string","minLength":1,"description":"Qwen-Audio system voice or registered cloned voice ID. For example longanhuan_v3.6."},
         "endpoint":{"type":"string","description":"Complete API endpoint URL. Leave unset to use the official endpoint."},
         "region":{"type":"string","enum":["singapore","beijing"],"default":"singapore","x-visibleWhen":{"property":"endpoint","values":[""],"default":""}},
         "workspaceId":{"type":"string","minLength":1,"maxLength":63,"x-requiredWhen":{"property":"endpoint","values":[""],"default":""},"x-visibleWhen":{"property":"endpoint","values":[""],"default":""}},
         "outputPath":{"type":"string","minLength":1},"outputFormat":{"type":"string","enum":["wav","mp3"],"default":"wav"},
         "sampleRate":{"type":"integer","enum":[8000,12000,16000,22050,24000,44100,48000],"default":24000},
         "volume":{"type":"integer","minimum":0,"maximum":100},"speed":{"type":"number","minimum":0.5,"maximum":2},"pitch":{"type":"number","minimum":0.5,"maximum":2},
         "seed":{"type":"integer","minimum":0,"maximum":65535},"instructions":{"type":"string"},"enableSsml":{"type":"boolean"},
         "languageCode":{"type":"string","enum":["zh","en","fr","de","ja","ko","ru","pt","th","id","vi","es","it","ms","fil","ar"]},
         "downloadTimeoutMs":{"type":"integer","minimum":1,"maximum":3600000,"default":300000},"maximumBytes":{"type":"integer","minimum":1,"maximum":2147483647,"default":104857600}}
        """;
}
