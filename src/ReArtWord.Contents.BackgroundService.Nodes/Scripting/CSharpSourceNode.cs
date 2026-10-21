using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>型付き非同期列挙を、一要素ごとに別の後続実行として通知します。</summary>
internal sealed class CSharpSourceNode : ISourceNode
{
    private readonly ScriptHostCommand host;
    private readonly ScriptNode script;

    /// <summary>単発C#と同じ編集・引数・パッケージ検査を使い、待受寿命を分けます。</summary>
    internal CSharpSourceNode(ScriptHostCommand host, ScriptPackageStore? packages = null)
    {
        this.host = host;
        script = new("csharp", host, packages, continuous: true);
        JsonObject schema = JsonNode.Parse(script.Descriptor.SettingsSchema.GetRawText())!.AsObject();
        schema["properties"]!.AsObject().Remove("timeoutMs");
        Descriptor = script.Descriptor with
        {
            Kind = ENodeKind.Source,
            Description = "Yield typed values from public static IAsyncEnumerable<T> Run(...). An optional CancellationToken is supplied by the workflow; each yield starts independent downstream work.",
            SettingsSchema = JsonSerializer.SerializeToElement(schema),
        };
    }

    /// <summary>継続待受の型付き入出力と一要素の容量上限です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>単発実行と共通の契約検査に非同期列挙の型検査を適用します。</summary>
    /// <returns>保存前に解消する問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => script.Validate(definition);

    /// <summary>固定したコードと引数で待ち受け、各要素の値を後続へ独立して渡します。</summary>
    /// <returns>正常完了・取消・実行失敗・停止不明を区別した結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        bool dispatched = false;
        try
        {
            if (NodeSupport.Invalid(context.Initial, this) is { } invalid)
            {
                return invalid;
            }
            ScriptAnalysis confirmed = ScriptSettings.Confirmed(Descriptor.Type, context.Initial.Settings)!;
            ScriptPackageEnvironment environment = script.Environment(confirmed);
            ScriptAnalysisRequest request = ScriptSettings.Request(Descriptor.Type, context.Initial.Settings);
            (ScriptAnalysis analysis, byte[]? assembly) = CSharpScriptCompiler.Emit(request.Code, environment, continuous: true);
            if (assembly is null || analysis.Contract is not { } contract || !ScriptSettings.Matches(confirmed.Contract!, contract))
            {
                return NodeResult.Failure("script.analysisRequired", "実行先の解析結果と保存した型契約が一致しません。", EOutcome.NotExecuted);
            }
            NodeValue arguments = ScriptArguments.NormalizeObject(ScriptNode.Arguments(context.Initial, contract), contract);
            ScriptBinaryValues.ValidateArguments(arguments, contract);
            ValueTypeDefinition argumentType = new("object", Properties: contract.Arguments.ToDictionary(pair => pair.Key, pair => ScriptSettings.JsonType(pair.Value), StringComparer.Ordinal));
            arguments = PathTypeMetadata.Apply(arguments, arguments.DataType ?? argumentType, OperatingSystem.IsWindows());
            JsonObject supplied = JsonNode.Parse(arguments.Json.GetRawText())!.AsObject();
            foreach (string name in contract.Arguments.Keys)
            {
                if (!supplied.ContainsKey(name))
                {
                    supplied[name] = null;
                }
            }
            NodeSettings settings = new(context.Initial.Settings);
            int maximum = settings.ReadInt32("maxBytes", 1048576);
            using ScriptWorkspace workspace = new();
            string assemblyPath = await workspace.WriteAssemblyAsync(assembly, lifetime.Token).ConfigureAwait(false);
            CSharpWorkerRequest worker = new(assemblyPath, JsonSerializer.SerializeToElement(supplied), environment.Assemblies, environment.NativeLibraries, maximum);
            NodeResult terminal = NodeResult.Success();
            await context.ReportStatusAsync(new(ESourceState.Listening), lifetime.Token).ConfigureAwait(false);
            dispatched = true;
            await CSharpSourceProcess.RunAsync(host, workspace, worker, settings.ReadInt32("maxMemoryMb", 256) * 1048576L, async (frame, token) =>
            {
                string? kind = frame.GetProperty("kind").GetString();
                string completed = "completed", failed = "failed", item = "item", exceptionKey = "exception";
                if (string.Equals(kind, completed, StringComparison.Ordinal))
                {
                    return false;
                }
                if (string.Equals(kind, failed, StringComparison.Ordinal))
                {
                    if (frame.GetProperty(exceptionKey).Deserialize<ExceptionDiagnostic>(ContractJson.Options) is { } diagnostic)
                    {
                        await NodeExecutionLog.WriteExceptionAsync(context.Initial, "script.executionFailed", diagnostic, CancellationToken.None).ConfigureAwait(false);
                    }
                    terminal = NodeResult.Failure("script.executionFailed", "継続スクリプトが例外で終了しました。実行Peerのログを確認してください。");
                    return false;
                }
                if (!string.Equals(kind, item, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("script.frameInvalid");
                }
                if (context.IsStopRequested)
                {
                    return false;
                }
                NodeValue value = ScriptBinaryValues.Result(frame, contract.Result);
                await context.EmitAsync(context.BeginItem(), NodeResult.Success(PathTypeMetadata.Apply(value, contract.Result)), token).ConfigureAwait(false);
                return true;
            }, lifetime.Token).ConfigureAwait(false);
            return terminal;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return cancellationToken.IsCancellationRequested ? NodeResult.Failure("script.cancelled", "継続待受を取り消しました。", EOutcome.Cancelled)
                                                             : NodeResult.Success();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException or JsonException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or ProcessStopUnconfirmedException)
        {
            await NodeExecutionLog.WriteExceptionAsync(context.Initial, "script.operationUnconfirmed", exception, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return NodeResult.Failure("script.operationUnconfirmed", "継続スクリプトの完了を確認できません。外部副作用の再実行は行いません。", dispatched ? EOutcome.Unknown
                                                                                                                       : EOutcome.NotExecuted);
        }
    }
}
