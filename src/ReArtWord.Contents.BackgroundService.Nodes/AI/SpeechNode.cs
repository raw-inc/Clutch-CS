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

/// <summary>選択した音声APIで生成し、完成したWAVファイルを検証します。</summary>
internal sealed class SpeechNode : AiOperationNode
{
    private static string EndpointKey => "endpoint";
    private static string QwenProvider => "qwen";
    private static string QwenModel => "qwen3-tts-vc-2026-01-22";
    private readonly bool qwen;

    /// <summary>OpenAIとQwenの音声生成ノードを作ります。</summary>
    internal SpeechNode(ISecretStore secrets, AiOptions options, MediaOptions media, bool qwen = false, bool clone = true) : base(secrets, options, media, TypeName(qwen, clone), Properties(qwen, clone), ["voice", "outputPath"], category: "AudioGeneration", allowAnonymousEndpoint: true)
    {
        this.qwen = qwen;
    }

    /// <summary>既存OpenAI識別子を維持してQwenを独立公開します。</summary>
    /// <returns>保存定義に使用するノード識別子です。</returns>
    private static string TypeName(bool qwen, bool clone) => (qwen, clone) switch
    {
        (true, true) => "ai.audio.qwen.generate",
        (true, false) => "ai.audio.qwen.tts.generate",
        _ => "ai.audio.generate",
    };

    /// <summary>共有の出力設定に、必要なサービス提供元固有項目だけを加えます。</summary>
    /// <returns>ノード種別に固定された設定スキーマです。</returns>
    private static string Properties(bool qwen, bool clone)
    {
        JsonObject properties = JsonNode.Parse("""{"voice":{"type":"string","minLength":1},"outputPath":{"type":"string","minLength":1},"downloadTimeoutMs":{"default":300000,"type":"integer","minimum":1,"maximum":3600000},"maximumBytes":{"default":104857600,"type":"integer","minimum":1,"maximum":2147483647}}""")!.AsObject();
        properties["endpoint"] = JsonNode.Parse("""{"type":"string","description":"Complete API endpoint URL. Leave unset to use the official endpoint."}""");
        if (qwen)
        {
            properties["voice"]!["description"] = "Voice ID registered for this model and API region.";
            JsonArray models = new(QwenModel);
            string model = QwenModel;
            if (!clone)
            {
                models = new("qwen3-tts-flash", "qwen3-tts-flash-2025-11-27", "qwen3-tts-flash-2025-09-18");
                model = "qwen3-tts-flash";
                properties["voice"]!["description"] = "Built-in Qwen voice name, for example Cherry.";
            }
            properties["model"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["examples"] = models, ["default"] = model };
            properties["region"] = JsonNode.Parse("""{"type":"string","enum":["singapore","beijing"],"default":"singapore","x-visibleWhen":{"property":"endpoint","values":[""],"default":""}}""");
            properties["languageType"] = JsonNode.Parse("""{"type":"string","enum":["Auto","Chinese","English","German","Italian","Portuguese","Spanish","Japanese","Korean","French","Russian"],"default":"Auto"}""");
        }
        else
        {
            properties["instructions"] = new JsonObject { ["type"] = "string" };
            properties["speed"] = JsonNode.Parse("""{"type":"number","minimum":0.25,"maximum":4}""");
            properties["outputFormat"] = JsonNode.Parse("""{"type":"string","enum":["wav","mp3"]}""");
        }
        return properties.ToJsonString();
    }

    /// <summary>異なるAPI用のモデルを誤ったサービス提供元へ送信しません。</summary>
    /// <returns>スキーマと対応モデルの問題です。</returns>
    protected override IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. base.ValidateSettings(definition)];
        NodeSettings settings = new(definition.Settings);
        string providerSuffix = ".aliyuncs.com", basePath = "/api/v1";
        if (qwen && Uri.TryCreate(settings.ReadText(EndpointKey), UriKind.Absolute, out Uri? endpoint)
            && endpoint.Host.EndsWith(providerSuffix, StringComparison.OrdinalIgnoreCase)
            && (endpoint.AbsolutePath.TrimEnd('/').Length == 0 || string.Equals(endpoint.AbsolutePath.TrimEnd('/'), basePath, StringComparison.Ordinal)))
        {
            issues.Add(new("ai.qwen.endpoint.path", "QwenのエンドポイントにはベースURLではなく、/api/v1/services/aigc/multimodal-generation/generation まで含む完全なURLを指定してください。", definition.Id, "/endpoint"));
        }
        string? model = settings.ReadText("model");
        if (!qwen && model?.StartsWith(QwenProvider, StringComparison.OrdinalIgnoreCase) == true)
        {
            issues.Add(new ValidationIssue("ai.audio.model", "Use the Qwen3-TTS-VC node for qwen3-tts-vc-2026-01-22. This node uses the OpenAI Speech API.", definition.Id));
        }
        string instructionsKey = "instructions";
        string[] legacyModels = ["tts-1", "tts-1-hd"];
        if (!qwen && legacyModels.Contains(model, StringComparer.Ordinal) && definition.Settings.TryGetProperty(instructionsKey, out _))
        {
            issues.Add(new("ai.audio.instructions", "Instructions are not supported by tts-1 or tts-1-hd.", definition.Id, "/instructions"));
        }
        return issues;
    }

    /// <summary>既存OpenAI設定の既定を維持し、Qwenだけ明示リージョンを使います。</summary>
    /// <returns>公式APIまたはローカル試験先です。</returns>
    protected override Uri ProviderBaseUri(NodeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ReadText(EndpointKey)))
        {
            return AiRequestSettings.Endpoint(settings, Options.BaseUri(false));
        }
        if (qwen)
        {
            return new Uri(Options.AlibabaBaseUri(settings.ReadText("region", "singapore")!), "api/v1/services/aigc/multimodal-generation/generation");
        }
        return new Uri(Options.BaseUri(false), "v1/audio/speech");
    }

    /// <summary>最終音声を保存してWAVのストリームを検証します。</summary>
    /// <returns>実ファイル情報です。</returns>
    protected override async Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string output = settings.ReadText("outputPath")!;
        string prompt = Prompt(context);
        if (qwen && 600 < prompt.EnumerateRunes().Count())
        {
            throw new ArgumentException("Qwen speech text must contain at most 600 characters.");
        }
        if (!qwen)
        {
            if (4096 < prompt.EnumerateRunes().Count())
            {
                throw new ArgumentException("OpenAI speech text must contain at most 4096 characters.");
            }
            string format = settings.ReadText("outputFormat", "wav")!;
            JsonObject request = new() { ["model"] = settings.ReadText("model"), ["input"] = prompt, ["voice"] = settings.ReadText("voice"), ["response_format"] = format };
            AiRequestSettings.Copy(context.Settings, request, ("instructions", "instructions"), ("speed", "speed"));
            string mediaType = format switch { "mp3" => "audio/mpeg", _ => "audio/wav" };
            return await AiAudioOutput.GenerateAsync(context, session, Media, ProviderBaseUri(settings), request, mediaType, cancellationToken).ConfigureAwait(false);
        }
        AiMediaOutput.RequireProbe(Media);
        string temporary = MediaArtifact.TemporaryPath(Media, output, allowExisting: true);
        try
        {
            Uri uri = await GenerateQwenAsync(settings, prompt, session, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource download = new(TimeSpan.FromMilliseconds(settings.ReadInt32("downloadTimeoutMs", 300000)));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(download.Token, cancellationToken);
            _ = await session.DownloadAsync(uri, null, temporary, new FileAccessPolicy(Media.Files), settings.ReadInt32("maximumBytes", 104857600), false, linked.Token).ConfigureAwait(false);
            session.TerminalKnown = true;
            return await MediaOutputFile.CommitAsync(Media, context, temporary, output, "audio/wav", "audio", cancellationToken: linked.Token).ConfigureAwait(false);
        }
        finally
        {
            MediaArtifact.Cleanup(Media, temporary);
        }
    }

    /// <summary>非ストリーミング合成の終端確認後に、認証不要の生成物URLを返します。</summary>
    /// <returns>検証した最終WAVの取得先です。</returns>
    private async Task<Uri> GenerateQwenAsync(NodeSettings settings, string prompt, AiRequestSession session, CancellationToken cancellationToken = default)
    {
        JsonElement response = await session.JsonAsync(ProviderBaseUri(settings).AbsoluteUri, new
        {
            model = settings.ReadText("model"),
            input = new { text = prompt, voice = settings.ReadText("voice"), language_type = settings.ReadText("languageType", "Auto") },
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        string codeKey = "code";
        if (response.TryGetProperty(codeKey, out JsonElement code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString()))
        {
            session.TerminalKnown = true;
            throw new InvalidDataException("Qwen speech returned a provider error.");
        }
        JsonElement output = response.GetProperty("output");
        string stopped = "stop";
        string? finishReason = output.GetProperty("finish_reason").GetString();
        if (!string.Equals(finishReason, stopped, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Qwen speech completion is not confirmed.");
        }
        session.TerminalKnown = true;
        Uri uri = new(output.GetProperty("audio").GetProperty("url").GetString()!);
        if (!string.IsNullOrWhiteSpace(settings.ReadText(EndpointKey)))
        {
            Uri endpoint = ProviderBaseUri(settings);
            if (uri.IsAbsoluteUri && string.Equals(uri.GetLeftPart(UriPartial.Authority), endpoint.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment))
            {
                return uri;
            }
        }
        return AiMediaOutput.AlibabaDownloadUri(Options, uri, upgradeHttp: true);
    }
}
