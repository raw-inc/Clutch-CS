using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>固定された媒体能力と明示設定から安全な変換引数を組み立てます。</summary>
// 抑制理由: 媒体種別と設定キーは保存設定および外部形式の固定契約です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "媒体種別と設定キーの定数は互換性を維持します。")]
internal sealed class MediaConverterNode : IFileFootprintNode
{
    private const string KeyAudio = "audio";
    private const string KeyPicture = "picture";
    private const string KeyMp3 = "mp3";
    private const string KeyFlac = "flac";
    private const string KeyJpeg = "jpeg";
    private const string KeyWebp = "webp";
    private const string KeyWebm = "webm";
    private const string KeyValue = "value";
    private const string KeyPeerId = "peerId";
    private const string KeyPath = "path";

    private readonly MediaOptions options;
    private readonly string kind;

    /// <summary>媒体の設定スキーマと実行場所を固定します。</summary>
    internal MediaConverterNode(MediaOptions options, string kind)
    {
        this.options = options;
        this.kind = kind;
        string formats = "[\"mp4\",\"webm\"]";
        if (string.Equals(kind, KeyAudio, StringComparison.Ordinal))
        {
            formats = "[\"wav\",\"mp3\",\"flac\"]";
        }
        if (string.Equals(kind, KeyPicture, StringComparison.Ordinal))
        {
            formats = "[\"png\",\"jpeg\",\"webp\"]";
        }
        string schema = """
            {"type":"object","required":["outputPath","format"],"properties":{
            "outputPath":{"type":"string","minLength":1},"format":{"type":"string","enum":FORMATS},
            "width":{"type":"integer","minimum":2,"maximum":8192},"height":{"type":"integer","minimum":2,"maximum":8192},
            "frameRate":{"type":"integer","minimum":1,"maximum":120},"sampleRate":{"default":48000,"type":"integer","minimum":8000,"maximum":192000},
            "channels":{"default":2,"type":"integer","minimum":1,"maximum":8},"timeoutMs":{"default":300000,"type":"integer","minimum":1,"maximum":86400000}}}
            """.Replace("FORMATS", formats, StringComparison.Ordinal);
        JsonObject schemaObject = JsonNode.Parse(schema)!.AsObject();
        JsonObject properties = schemaObject["properties"]!.AsObject();
        string[] ignored = ["sampleRate", "channels"];
        if (string.Equals(kind, KeyAudio, StringComparison.Ordinal))
        {
            ignored = ["width", "height", "frameRate"];
        }
        if (string.Equals(kind, KeyPicture, StringComparison.Ordinal))
        {
            ignored = ["sampleRate", "channels", "frameRate"];
        }
        foreach (string nameToRemove in ignored)
        {
            properties.Remove(nameToRemove);
        }
        string codecs = """{"type":"string","enum":["libx264","libx265","libvpx-vp9","libaom-av1"]}""";
        if (string.Equals(kind, KeyAudio, StringComparison.Ordinal))
        {
            codecs = """{"type":"string","enum":["pcm_s16le","pcm_s24le","pcm_f32le","libmp3lame","flac"]}""";
        }
        if (string.Equals(kind, KeyPicture, StringComparison.Ordinal))
        {
            codecs = """{"type":"string","enum":["png","mjpeg","libwebp"]}""";
        }
        properties["codec"] = JsonNode.Parse(codecs);
        MediaOutputFile.Configure(schemaObject);
        properties["codec"]!["x-choicesWhen"] = JsonNode.Parse("""{"property":"format","options":{"wav":["pcm_s16le","pcm_s24le","pcm_f32le"],"mp3":["libmp3lame"],"flac":["flac"],"mp4":["libx264","libx265"],"webm":["libvpx-vp9","libaom-av1"],"png":["png"],"jpeg":["mjpeg"],"webp":["libwebp"]}}""");
        Descriptor = new NodeDescriptor("media." + kind + ".convert", kind + "変換", "Media", "入力pathを実変換し検証済み出力を返します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new PortDescriptor(KeyValue, Required: true)], [new PortDescriptor(KeyValue)], ["success", "failure", "notExecuted", "cancelled", "unknown"], NodeSettings.Schema(schemaObject.ToJsonString())) { HasAsyncCompletion = true };
    }

    /// <summary>設定画面に媒体の能力を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>設定と媒体固有の寸法指定を検証します。</summary>
    /// <returns>不正な設定の一覧です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        IReadOnlyList<ValidationIssue> issues = NodeSchemaValidator.Validate(definition, Descriptor);
        if (issues.Count != 0)
        {
            return issues;
        }
        NodeSettings settings = new(definition.Settings);
        string? codec = settings.ReadText("codec");
        JsonElement choices = NodeSettingVisibility.Choices(Descriptor.SettingsSchema.GetProperty("properties").GetProperty("codec"), definition.Settings).GetProperty("enum");
        bool compatible = codec is null || choices.EnumerateArray().Any(choice => string.Equals(choice.GetString(), codec, StringComparison.Ordinal));
        if (!compatible)
        {
            return [new ValidationIssue("media.codecMismatch", "codecと出力formatの組合せが未対応です。", definition.Id, "codec")];
        }
        return [];
    }

    /// <summary>同じPeerの入力実体と出力変更を共通予約へ渡します。</summary>
    /// <returns>検証した読書き範囲。</returns>
    public ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => GetFileAccessCoreAsync(context, cancellationToken);

    /// <summary>生成物ハッシュと許可範囲を実行地点で確認します。</summary>
    /// <returns>入力と出力の範囲。</returns>
    private async ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessCoreAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(options.Files);
        string input = await FileArtifactAccess.ResolveInputAsync(context.PeerId, context.Inputs[KeyValue], policy, cancellationToken).ConfigureAwait(false);
        string output = policy.ResolvePath(new NodeSettings(context.Settings).ReadText("outputPath")!);
        return [new(context.PeerId, input, false), new(context.PeerId, output, true)];
    }

    /// <summary>変換終了と実ストリームを検査してから出力を確定します。</summary>
    /// <returns>変換自体の成否と実ファイル情報です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<ValidationIssue> issues = Validate(new NodeDefinition(context.NodeId, Descriptor.Type, context.Settings, context.Inputs));
        if (issues.Count != 0)
        {
            return NodeResult.Failure(issues[0].Code, issues[0].Message, EOutcome.NotExecuted);
        }
        string? temporary = null;
        bool started = false;
        string stage = "prepare";
        try
        {
            NodeSettings settings = new(context.Settings);
            FileAccessPolicy policy = new(options.Files);
            string input = policy.ResolvePath(ReadPath(context));
            string output = policy.ResolvePath(settings.ReadText("outputPath")!);
            NodeResult? preparation = MediaOutputFile.Prepare(options, context, "media");
            if (preparation is not null)
            {
                return preparation;
            }
            if (!File.Exists(options.FfmpegPath) || !File.Exists(options.FfprobePath) || !File.Exists(input))
            {
                return NodeResult.Failure("media.unavailable", "入力または明示メディア実行物がありません。", EOutcome.NotExecuted);
            }
            temporary = MediaArtifact.TemporaryPath(options, output, allowExisting: true);
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(settings.ReadInt32("timeoutMs", 300000)));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            stage = "input-probe";
            _ = await MediaArtifact.ProbeAsync(options, input, kind, linked.Token).ConfigureAwait(false);
            string format = settings.ReadText("format")!;
            List<string> arguments = Arguments(input, temporary, settings, format);
            policy.ValidatePath(input);
            policy.ValidatePath(temporary);
            started = true;
            stage = "conversion";
            _ = await MediaProcess.RunAsync(options, options.FfmpegPath, arguments, stage, linked.Token).ConfigureAwait(false);
            stage = "output-validation";
            return await MediaOutputFile.CommitAsync(options, context, temporary, output, MediaType(format), kind, "media", linked.Token).ConfigureAwait(false);
        }
        catch (ProcessStopUnconfirmedException exception)
        {
            MediaProcess.Failed(options, stage, exception);
            temporary = null;
            return NodeResult.Failure("media.stopUnconfirmed", "メディア処理の停止を確認できません。", EOutcome.Unknown);
        }
        catch (OperationCanceledException exception)
        {
            MediaProcess.Failed(options, stage, exception);
            if (cancellationToken.IsCancellationRequested)
            {
                return NodeResult.Failure("media.cancelled", "メディア処理を停止しました。", EOutcome.Cancelled);
            }
            return NodeResult.Failure("media.timeout", "メディア処理を期限内に完了できませんでした。");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        {
            MediaProcess.Failed(options, stage, exception);
            EOutcome outcome = EOutcome.NotExecuted;
            if (started)
            {
                outcome = EOutcome.Failed;
            }
            return NodeResult.Failure("media.failed", "メディア入力、実行または出力検証に失敗しました。", outcome);
        }
        finally
        {
            MediaArtifact.Cleanup(options, temporary);
        }
    }

    /// <summary>入力文字列または生成物のパスを取得します。</summary>
    /// <returns>現在のPeerで参照する入力パスです。</returns>
    private static string ReadPath(NodeExecutionContext context)
    {
        if (!context.Inputs.TryGetValue(KeyValue, out NodeValue? value))
        {
            throw new ArgumentException("入力がありません。");
        }
        if (value.Json.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }
        if (value.Json.ValueKind == JsonValueKind.Object && value.Json.TryGetProperty(KeyPeerId, out JsonElement peer) && string.Equals(peer.GetString(), context.PeerId, StringComparison.Ordinal) && value.Json.TryGetProperty(KeyPath, out JsonElement path))
        {
            return path.GetString()!;
        }
        throw new ArgumentException("入力pathまたは同一Peerの生成物が必要です。");
    }

    /// <summary>媒体に適合する固定codecと数値設定だけから引数を作ります。</summary>
    /// <returns>shell展開されない引数列です。</returns>
    private List<string> Arguments(string input, string output, NodeSettings settings, string format)
    {
        List<string> arguments = ["-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-protocol_whitelist", "file", "-format_whitelist", "wav,mp3,flac,ogg,mov,matroska,webm,avi,png_pipe,jpeg_pipe,webp_pipe", "-i", input, "-map_metadata", "-1"];
        if (string.Equals(kind, KeyAudio, StringComparison.Ordinal))
        {
            arguments.AddRange(["-map", "0:a:0", "-vn"]);
            string codec = "pcm_s16le";
            if (string.Equals(format, KeyMp3, StringComparison.Ordinal))
            {
                codec = "libmp3lame";
            }
            if (string.Equals(format, KeyFlac, StringComparison.Ordinal))
            {
                codec = KeyFlac;
            }
            arguments.AddRange(["-c:a", settings.ReadText("codec", codec)!, "-ar", settings.ReadInt32("sampleRate", 48000).ToString(CultureInfo.InvariantCulture), "-ac", settings.ReadInt32("channels", 2).ToString(CultureInfo.InvariantCulture), "-f", format]);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0"]);
            if (string.Equals(kind, KeyPicture, StringComparison.Ordinal))
            {
                string codec = format;
                if (string.Equals(format, KeyJpeg, StringComparison.Ordinal))
                {
                    codec = "mjpeg";
                }
                if (string.Equals(format, KeyWebp, StringComparison.Ordinal))
                {
                    codec = "libwebp";
                }
                arguments.AddRange(["-frames:v", "1", "-an", "-c:v", settings.ReadText("codec", codec)!, "-f", "image2", "-update", "1"]);
            }
            else
            {
                string codec = "libx264";
                string audioCodec = "aac";
                if (string.Equals(format, KeyWebm, StringComparison.Ordinal))
                {
                    codec = "libvpx-vp9";
                    audioCodec = "libopus";
                }
                arguments.AddRange(["-map", "0:a:0?", "-c:v", settings.ReadText("codec", codec)!, "-c:a", audioCodec, "-pix_fmt", "yuv420p", "-f", format]);
                int frameRate = settings.ReadInt32("frameRate");
                if (0 < frameRate)
                {
                    arguments.AddRange(["-r", frameRate.ToString(CultureInfo.InvariantCulture)]);
                }
            }
            int width = settings.ReadInt32("width", -2);
            int height = settings.ReadInt32("height", -2);
            if (0 < width || 0 < height)
            {
                arguments.AddRange(["-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}")]);
            }
        }
        arguments.Add(output);
        return arguments;
    }

    /// <summary>対応する出力形式のMIME種別を取得します。</summary>
    /// <returns>生成物へ記録する種別です。</returns>
    private static string MediaType(string format)
    {
        return format switch
        {
            "wav" => "audio/wav",
            KeyMp3 => "audio/mpeg",
            KeyFlac => "audio/flac",
            "mp4" => "video/mp4",
            KeyWebm => "video/webm",
            "png" => "image/png",
            KeyJpeg => "image/jpeg",
            KeyWebp => "image/webp",
            _ => throw new ArgumentException("未対応形式です。", nameof(format)),
        };
    }
}
