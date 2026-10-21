using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>AI処理に共通の秘密解決・期限・結果不明境界を統一します。</summary>
internal abstract class AiOperationNode : IFileFootprintNode
{
    private readonly ISecretStore secrets;
    private readonly bool wan;

    /// <summary>サービスごとの接続実装の設定を共通通信設定に統合します。</summary>
    protected AiOperationNode(ISecretStore secrets, AiOptions options, MediaOptions mediaOptions, string type, string properties, string[] required, bool wan = false, string category = "AI", IReadOnlyList<PortDescriptor>? outputs = null, bool inputRequired = true, IReadOnlyList<PortDescriptor>? inputs = null, bool modelRequired = true, bool allowAnonymousEndpoint = false)
    {
        this.secrets = secrets;
        this.wan = wan;
        Options = options;
        Media = mediaOptions;
        JsonObject schema = JsonNode.Parse("""{"type":"object","required":["model","secretRef","interfaceId","localAddress"],"properties":{"model":{"type":"string","minLength":1},"secretRef":{"type":"string","minLength":1},"interfaceId":{"type":"string","minLength":1},"localAddress":{"type":"string","minLength":1},"timeoutMs":{"type":"integer","minimum":1,"maximum":86400000,"default":600000}}}""")!.AsObject();
        foreach ((string name, JsonNode? value) in JsonNode.Parse(properties)!.AsObject())
        {
            schema["properties"]![name] = value!.DeepClone();
        }
        foreach (string name in required)
        {
            schema["required"]!.AsArray().Add(name);
        }
        foreach ((string name, bool needed) in new[] { ("model", modelRequired), ("secretRef", !allowAnonymousEndpoint) })
        {
            if (!needed)
            {
                JsonArray requiredItems = schema["required"]!.AsArray();
                requiredItems.Remove(requiredItems.First(item => string.Equals(item!.GetValue<string>(), name, StringComparison.Ordinal)));
            }
        }
        if (allowAnonymousEndpoint)
        {
            schema["properties"]!["secretRef"]!["x-requiredWhen"] = JsonNode.Parse("""{"property":"endpoint","values":[""],"default":""}""");
        }
        MediaOutputFile.Configure(schema);
        string requiredKey = "required", conditionalKey = "x-requiredWhen";
        foreach ((string name, JsonNode? property) in schema["properties"]!.AsObject())
        {
            if (!schema[requiredKey]!.AsArray().Any(item => string.Equals(item!.GetValue<string>(), name, StringComparison.Ordinal))
                && property is JsonObject field && !field.ContainsKey(conditionalKey))
            {
                field[OptionalSettingValues.Marker] = true;
            }
        }
        Descriptor = new NodeDescriptor(type, type, category, "公式APIで最終結果を取得します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer, inputs ?? [new PortDescriptor("value", Required: inputRequired)], outputs ?? [new PortDescriptor("value")], ["success", "failure", "notExecuted", "unknown", "cancelled", "indeterminate"], NetworkBindingSettings.Schema(NodeSettings.Schema(schema.ToJsonString())))
        {
            HasAsyncCompletion = true,
            IsHidden = type is "ai.text.generate" or "ai.picture.generate" or "ai.video.generate",
        };
    }

    /// <summary>固定されたサービス提供元接続方針です。</summary>
    protected AiOptions Options
    {
        get;
    }

    /// <summary>固定されたメディア実行物と許可範囲です。</summary>
    protected MediaOptions Media
    {
        get;
    }

    /// <summary>Bearer以外を使うAPIだけ、固定のAPIキーヘッダー名を指定します。</summary>
    protected virtual string? ApiKeyHeader => null;

    /// <summary>編集と実行に共通の設定仕様です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>公開された設定スキーマを検証します。</summary>
    /// <returns>不正な設定の一覧です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ValidateSettings(definition with { Settings = OptionalSettingValues.Normalize(definition.Settings, Descriptor.SettingsSchema) });
    }

    /// <summary>任意nullを除いた同じ設定で、共通条件とサービス固有の条件を検証します。</summary>
    /// <returns>正規化後に訂正が必要な設定です。</returns>
    protected virtual IReadOnlyList<ValidationIssue> ValidateSettings(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. NodeSchemaValidator.Validate(definition, Descriptor), .. NetworkBindingSettings.Validate(definition)];
        string endpointKey = "endpoint";
        if (definition.Settings.ValueKind == JsonValueKind.Object && definition.Settings.TryGetProperty(endpointKey, out JsonElement endpoint) && endpoint.ValueKind == JsonValueKind.String)
        {
            try
            {
                _ = AiRequestSettings.Endpoint(new(definition.Settings), new Uri("https://api.openai.com/"));
            }
            catch (FormatException)
            {
                issues.Add(new("ai.endpoint.invalid", "Specify a complete HTTP or HTTPS API endpoint without user information or a fragment.", definition.Id, "/endpoint"));
            }
        }
        string propertiesKey = "properties", requiredWhenKey = "x-requiredWhen";
        JsonElement properties = Descriptor.SettingsSchema.GetProperty(propertiesKey);
        if (definition.Settings.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (property.Value.TryGetProperty(requiredWhenKey, out _) && NodeSettingVisibility.IsRequired(property.Value, definition.Settings)
                    && !definition.Settings.TryGetProperty(property.Name, out _))
                {
                    issues.Add(new("settings.required", "必須設定がありません。", definition.Id, "/" + property.Name));
                }
            }
        }
        return issues;
    }

    /// <summary>外部AI呼出し前に実ファイルの入力・出力予約範囲を取得します。</summary>
    /// <returns>ファイルを使わない処理では空集合。</returns>
    public ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => GetFileAccessCoreAsync(context, cancellationToken);

    /// <summary>生成物の所有Peerとハッシュも実行地点で検証します。</summary>
    /// <returns>検証済みファイル範囲。</returns>
    private async ValueTask<IReadOnlyList<FilePathAccess>> GetFileAccessCoreAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(Media.Files);
        NodeSettings settings = new(context.Settings);
        List<FilePathAccess> accesses = [];
        string? output = settings.ReadText("outputPath");
        if (output is not null)
        {
            accesses.Add(new FilePathAccess(context.PeerId, policy.ResolvePath(output), true));
        }
        string? mediaKind = settings.ReadText("mediaKind");
        string textKind = "text";
        string valueKey = "value";
        if (mediaKind is not null && !string.Equals(mediaKind, textKind, StringComparison.Ordinal) && context.Inputs.TryGetValue(valueKey, out NodeValue? value) && value.Kind != EValueKind.Binary)
        {
            string input = await FileArtifactAccess.ResolveInputAsync(context.PeerId, value, policy, cancellationToken).ConfigureAwait(false);
            accesses.Add(new FilePathAccess(context.PeerId, input, false));
            string videoKind = "video";
            if (string.Equals(mediaKind, videoKind, StringComparison.Ordinal))
            {
                string directory = Path.GetDirectoryName(input) ?? throw new IOException("動画入力の親ディレクトリがありません。");
                accesses.Add(new FilePathAccess(context.PeerId, policy.ResolvePath(directory, true), true, true));
            }
        }
        return accesses;
    }

    /// <summary>一回のAPI実行を固定設定と秘密参照で処理します。</summary>
    /// <returns>受理通知ではなく最終取得結果、または明示した未確定結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = context with { Settings = OptionalSettingValues.Normalize(context.Settings, Descriptor.SettingsSchema) };
        IReadOnlyList<ValidationIssue> issues = Validate(new NodeDefinition(context.NodeId, Descriptor.Type, context.Settings, context.Inputs));
        if (issues.Count != 0)
        {
            return NodeResult.Failure(issues[0].Code, issues[0].Message, EOutcome.NotExecuted);
        }
        AiRequestSession? session = null;
        try
        {
            NodeSettings settings = new(context.Settings);
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(settings.ReadInt32("timeoutMs", 600000)), context.TimeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (MediaOutputFile.Prepare(Media, context) is { } preparation)
            {
                return preparation;
            }
            string? reference = settings.ReadText("secretRef");
            string? secret = reference is null ? string.Empty
                                               : await secrets.GetAsync(reference, linked.Token).ConfigureAwait(false);
            string? endpoint = settings.ReadText("endpoint");
            if (string.IsNullOrWhiteSpace(secret) && (reference is not null || string.IsNullOrWhiteSpace(endpoint)))
            {
                return NodeResult.Failure("ai.secretUnavailable", "指定された秘密を解決できません。", EOutcome.NotExecuted);
            }
            try
            {
                session = new AiRequestSession(context, ProviderBaseUri(settings), secret ?? string.Empty, ApiKeyHeader);
            }
            catch (System.Net.Sockets.SocketException exception)
            {
                await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "ai.networkBindingUnavailable", exception, false, CancellationToken.None).ConfigureAwait(false);
                return NodeResult.Failure("ai.networkBindingUnavailable", "指定されたNICまたはローカルIPv4アドレスを使用できないため、APIへ送信していません。実行先PCのネットワーク設定を確認してください。", EOutcome.NotExecuted)
                    with { FileAccessCompleted = true };
            }
            return await PerformAsync(context, session, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "ai.incomplete", exception, false, CancellationToken.None).ConfigureAwait(false);
            }
            return Incomplete(session, cancellationToken.IsCancellationRequested, exception) with { FileAccessCompleted = true };
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FormatException or InvalidDataException or IOException or HttpRequestException or JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ProcessStopUnconfirmedException)
        {
            await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "ai.incomplete", exception, false, CancellationToken.None).ConfigureAwait(false);
            if (exception is ProcessStopUnconfirmedException)
            {
                return NodeResult.Failure("ai.stopUnconfirmed", "AI入出力に使用した外部プロセスの停止を確認できません。", EOutcome.Unknown)
                    with { Outputs = Incomplete(session, false).Outputs };
            }
            return Incomplete(session, false, exception) with { FileAccessCompleted = true };
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>接続先サービスの最終結果を取得します。</summary>
    /// <returns>操作自身の結果です。</returns>
    protected abstract Task<NodeResult> PerformAsync(NodeExecutionContext context, AiRequestSession session, CancellationToken cancellationToken = default);

    /// <summary>検証済みの設定からサービス提供元の公式接続先を選びます。</summary>
    /// <returns>一回の処理に固定するbase URIです。</returns>
    protected virtual Uri ProviderBaseUri(NodeSettings settings) => Options.BaseUri(wan);

    /// <summary>入力文字列を取得し空の生成要求を拒否します。</summary>
    /// <returns>サービス提供元へ渡す明示入力です。</returns>
    protected static string Prompt(NodeExecutionContext context)
    {
        context.Inputs.TryGetValue("value", out NodeValue? value);
        string? prompt = value?.GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return prompt;
    }

    /// <summary>送信前後とサービス提供元終端確認を区別して失敗を返します。</summary>
    /// <returns>ジョブが受理済みなら照会用IDを含む結果です。</returns>
    private static NodeResult Incomplete(AiRequestSession? session, bool cancelled, Exception? error = null)
    {
        EOutcome outcome = EOutcome.NotExecuted;
        if (cancelled)
        {
            outcome = EOutcome.Cancelled;
        }
        if (session?.Dispatched == true || session?.JobId is not null)
        {
            outcome = EOutcome.Unknown;
            if (session.TerminalKnown)
            {
                outcome = EOutcome.Failed;
            }
        }
        string diagnostic = session?.DescribeFailure(error) ?? "stage=preparation; " + AiFailureDiagnostics.Describe(error);
        string cancellation = string.Empty;
        if (cancelled)
        {
            cancellation = " 明示取消を受け付けました。";
        }
        if (!cancelled && error is OperationCanceledException)
        {
            cancellation = " 処理の制限時間に達しました。";
        }
        NodeResult result = NodeResult.Failure("ai.incomplete", "AI結果を確定取得できませんでした。送信済み要求は暗黙に再生成しません。" + cancellation + " " + diagnostic, outcome);
        if (session?.JobId is not null)
        {
            return result with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["job"] = NodeValue.FromObject(new { id = session.Redact(session.JobId), remoteCancellationConfirmed = false }) } };
        }
        return result;
    }
}
