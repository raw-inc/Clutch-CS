using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.JavaScript;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>型確認済みコードの一回を、所有する独立プロセスで実行します。</summary>
internal sealed class ScriptNode : IActionNode
{
    private readonly bool python;
    private readonly bool javascript;
    private readonly bool continuous;
    private readonly ScriptHostCommand host;
    private readonly ScriptPackageStore? packages;
    private readonly ScriptRuntimeRegistry? runtimes;

    /// <summary>言語とCoreワーカーの所在を固定します。</summary>
    internal ScriptNode(string language, ScriptHostCommand host, ScriptPackageStore? packages = null, ScriptRuntimeRegistry? runtimes = null, bool continuous = false)
    {
        python = string.Equals(language, "python", StringComparison.Ordinal);
        javascript = string.Equals(language, "javascript", StringComparison.Ordinal);
        this.host = host;
        this.continuous = continuous;
        this.packages = packages;
        this.runtimes = runtimes;
        string type = "script." + language;
        if (continuous)
        {
            type += "-source";
        }
        Descriptor = new(type, type, "Scripting", "Run typed trusted code in an owned process with a deadline and output limit. External side effects are never automatically retried.",
            ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new("arguments", EPortValueKind.Json)], [new("value", !python && !javascript ? EPortValueKind.Any
                                                                                                                                             : EPortValueKind.Json)], ["success"], NodeSettings.Schema("""
            {"type":"object","required":["code","analysis"],"properties":{
            "inputMode":{"x-runtime-input":false,"x-script-metadata":true,"type":"string","enum":["individual","object"],"default":"individual"},
            "code":{"x-runtime-input":false,"type":"string","minLength":1,"maxLength":65536,"x-script-code":true},
            "analysis":{"x-runtime-input":false,"type":"object","x-script-metadata":true},"manualContract":{"x-runtime-input":false,"type":"object","x-script-metadata":true},"pythonPath":{"x-runtime-input":false,"type":"string"},"nodePath":{"x-runtime-input":false,"type":"string"},
            "timeoutMs":{"type":"integer","minimum":1,"maximum":600000,"default":30000},
            "maxBytes":{"type":"integer","minimum":1024,"maximum":16777216,"default":1048576},
            "maxMemoryMb":{"type":"integer","minimum":64,"maximum":4096,"default":256}}}
            """)) { HasAsyncCompletion = true };
        JsonObject schema = JsonNode.Parse(Descriptor.SettingsSchema.GetRawText())!.AsObject();
        if (!python)
        {
            schema["properties"]!.AsObject().Remove("pythonPath");
        }
        if (!javascript)
        {
            schema["properties"]!.AsObject().Remove("nodePath");
        }
        if (!python && !javascript)
        {
            schema["properties"]!.AsObject().Remove("manualContract");
        }
        Descriptor = Descriptor with { SettingsSchema = JsonSerializer.SerializeToElement(schema) };
    }

    /// <summary>型確認と実行上限の共通設定です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>コードと解析の一致を要求し、標準参照のC#は保存前にも専用Analyzerで照合します。</summary>
    /// <returns>修正または再解析が必要な問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        IReadOnlyList<ValidationIssue> issues = NodeSupport.Validate(definition, Descriptor);
        if (issues.Count != 0)
        {
            return issues;
        }
        ScriptAnalysis? confirmed = ScriptSettings.Confirmed(definition.Type, definition.Settings);
        if (confirmed?.Contract is null)
        {
            return [new("script.analysisRequired", "現在のコードと型選択に対する解析が必要です。", definition.Id)];
        }
        try
        {
            ScriptAnalysisService.Validate(confirmed.Contract, allowBinary: !python && !javascript);
            if (confirmed.PackageEnvironmentId is not null && !ScriptPackageRules.Revision(confirmed.PackageEnvironmentId))
            {
                return [new("script.packages.invalidRevision", "パッケージ構成の識別子が不正です。再解析してください。", definition.Id)];
            }
            string arguments = "arguments";
            bool individual = ScriptInputPorts.Individual(definition.Settings);
            foreach ((string port, NodeValue value) in definition.Inputs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal))
            {
                string? argument = ScriptInputPorts.Argument(port);
                if (individual && string.Equals(port, arguments, StringComparison.Ordinal) || argument is not null && (!individual || !confirmed.Contract.Arguments.ContainsKey(argument)))
                {
                    return [new("script.inputModeMismatch", "選択した引数の入力方式に対応しない入力があります。", definition.Id, port)];
                }
                if (argument is not null)
                {
                    string text = "string";

                    // LEGACY-FORMAT: csharp-byte-array-string — 後続の専用解析でbyte[]かを確認するまで、旧文字列契約でバイナリを拒否しません。
                    if (!python && !javascript && confirmed.ValueProtocolVersion == 0 && value.Kind == EValueKind.Binary
                        && string.Equals(confirmed.Contract.Arguments[argument].Type, text, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    _ = ScriptBinaryValues.Argument(ScriptArguments.Normalize(value, argument, confirmed.Contract), confirmed.Contract.Arguments[argument]);
                }
            }
            if (!individual && definition.Inputs?.TryGetValue(arguments, out NodeValue? supplied) == true)
            {
                ScriptBinaryValues.ValidateArguments(ScriptArguments.NormalizeObject(supplied, confirmed.Contract), confirmed.Contract);
            }

            // 共有定義の保存元と実行先は別Peerになり得ます。固定構成の意味検査は解析APIと実行先で行います。
            if (!python && !javascript && confirmed.PackageEnvironmentId is null)
            {
                ScriptAnalysis actual = CSharpScriptCompiler.Analyze(ScriptSettings.Request(definition.Type, definition.Settings).Code, continuous: continuous);
                if (actual.Contract is null || !ScriptSettings.Matches(confirmed.Contract, actual.Contract, legacyBinary: confirmed.ValueProtocolVersion == 0))
                {
                    return [new("script.contractMismatch", "専用Analyzerの入出力契約と保存された解析が一致しません。", definition.Id)];
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return [new("script.contractInvalid", "WorkflowEditorで扱える入出力型を指定してください。", definition.Id)];
        }
        return [];
    }

    /// <summary>実行先でも再検査し、引数と結果を同じ型契約で確認します。</summary>
    /// <returns>確認できた結果または副作用不明です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool dispatched = false;
        string unavailable = "script.packages.environmentUnavailable";
        string unsafeNumber = "script.javascriptUnsafeNumber";
        string runtimePrefix = "script.runtime.";
        try
        {
            if (NodeSupport.Invalid(context, this) is { } invalid)
            {
                return invalid;
            }
            ScriptAnalysisRequest request = ScriptSettings.Request(Descriptor.Type, context.Settings);
            ScriptAnalysis confirmed = ScriptSettings.Confirmed(Descriptor.Type, context.Settings)!;
            ScriptPackageEnvironment environment = Environment(confirmed);
            if (runtimes is not null)
            {
                environment = await runtimes.BindAsync(request, environment, cancellationToken).ConfigureAwait(false);
            }
            ScriptAnalysis checkedCode = await ScriptAnalysisService.AnalyzeAsync(request, environment, cancellationToken).ConfigureAwait(false);
            ScriptContract? contract = checkedCode.Contract;
            ScriptContract? saved = ScriptSettings.Confirmed(Descriptor.Type, context.Settings)?.Contract;
            if (contract is null || saved is null || !ScriptSettings.Matches(saved, contract, legacyBinary: !python && !javascript && confirmed.ValueProtocolVersion == 0))
            {
                return NodeResult.Failure("script.analysisRequired", "実行先のコード解析結果と保存契約が一致しません。", EOutcome.NotExecuted);
            }
            NodeValue arguments = ScriptArguments.NormalizeObject(Arguments(context, contract), contract);
            ValueTypeDefinition argumentType = new("object", Properties: contract.Arguments.ToDictionary(pair => pair.Key, pair => ScriptSettings.JsonType(pair.Value), StringComparer.Ordinal));
            ScriptBinaryValues.ValidateArguments(arguments, contract);
            if (javascript)
            {
                JavaScriptRuntime.ValidateNumbers(arguments.Json);
            }
            if (arguments.DataType is not null)
            {
                arguments = PathTypeMetadata.Apply(arguments, arguments.DataType, OperatingSystem.IsWindows());
            }
            if (PathTypeMetadata.Copy(argumentType) is not null)
            {
                arguments = PathTypeMetadata.Apply(arguments, argumentType, OperatingSystem.IsWindows());
            }
            JsonObject supplied = JsonNode.Parse(arguments.Json.GetRawText())!.AsObject();
            foreach (string name in contract.Arguments.Keys)
            {
                if (!javascript && !supplied.ContainsKey(name))
                {
                    supplied[name] = null;
                }
            }
            NodeSettings settings = new(context.Settings);
            int maximum = settings.ReadInt32("maxBytes", 1048576);
            using ScriptWorkspace workspace = new();
            string executable;
            IReadOnlyList<string> command;
            if (python)
            {
                executable = environment.PythonExecutable ?? environment.RuntimeExecutable ?? request.PythonPath ?? string.Empty;
                string runner = await workspace.WritePythonHostAsync(cancellationToken).ConfigureAwait(false);
                string input = await workspace.WriteAsync(new { request.Code, arguments = supplied, contract, maximumDiagnosticBytes = maximum }, maximum, cancellationToken).ConfigureAwait(false);
                command = ["-I", runner, "execute", input];
            }
            else
            {
                if (javascript)
                {
                    executable = environment.Info?.NodePath ?? environment.RuntimeExecutable ?? request.NodePath ?? string.Empty;
                    string runner = await workspace.WriteJavaScriptHostAsync(cancellationToken).ConfigureAwait(false);
                    string input = await workspace.WriteAsync(new { request.Code, arguments = supplied, argumentNames = contract.Arguments.Keys.ToArray(), contract.VariadicArgument, packageDirectory = environment.NpmDirectory, maximumDiagnosticBytes = maximum }, maximum, cancellationToken).ConfigureAwait(false);
                    command = [runner, "execute", input];
                }
                else
                {
                    (ScriptAnalysis analysis, byte[]? assembly) = CSharpScriptCompiler.Emit(request.Code, environment);
                    if (assembly is null || analysis.Contract is null)
                    {
                        return NodeResult.Failure("script.compilationFailed", "コードのコンパイルを完了できません。", EOutcome.NotExecuted);
                    }
                    string assemblyPath = await workspace.WriteAssemblyAsync(assembly, cancellationToken).ConfigureAwait(false);
                    string input = await workspace.WriteAsync(new CSharpWorkerRequest(assemblyPath, JsonSerializer.SerializeToElement(supplied), environment.Assemblies, environment.NativeLibraries, maximum), maximum, cancellationToken).ConfigureAwait(false);
                    executable = host.Executable;
                    command = [.. host.PrefixArguments, "--script-worker", input];
                }
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.ReadInt32("timeoutMs", 30000));
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            long memory = settings.ReadInt32("maxMemoryMb", 256) * 1048576L;
            ProcessRunLimits limits = new(maximum, true, memory, workspace.DirectoryPath);
            if (python)
            {
                limits = PythonRuntime.Limits(maximum, memory, workspace.DirectoryPath);
            }
            if (javascript)
            {
                limits = JavaScriptRuntime.Limits(maximum, memory, workspace.DirectoryPath, executable);
            }
            string output = await BoundedProcessRunner.RunAsync(executable, command, limits, timeout.Token).ConfigureAwait(false);
            if (maximum < Encoding.UTF8.GetByteCount(output))
            {
                throw new InvalidDataException("script.outputLimit");
            }
            using JsonDocument response = JsonDocument.Parse(output);
            string completed = "completed";
            if (!response.RootElement.TryGetProperty(completed, out JsonElement success) || success.ValueKind != JsonValueKind.True)
            {
                string exceptionProperty = "exception";
                if (response.RootElement.TryGetProperty(exceptionProperty, out JsonElement details)
                    && details.Deserialize<Runtime.Observability.ExceptionDiagnostic>(ContractJson.Options) is { } diagnostic)
                {
                    await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "script.executionFailed", diagnostic, CancellationToken.None).ConfigureAwait(false);
                }
                return NodeResult.Failure("script.executionFailed", "スクリプトが例外で終了しました。実行Peerのログで例外診断を確認してください。");
            }
            NodeValue value = ScriptBinaryValues.Result(response.RootElement, contract.Result);
            value = PathTypeMetadata.Apply(value, contract.Result);
            return NodeResult.Success(value);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException or JsonException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or ProcessStopUnconfirmedException)
        {
            if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await Runtime.Observability.NodeExecutionLog.WriteExceptionAsync(context, "script.operationUnconfirmed", exception, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            if (!dispatched && string.Equals(exception.Message, unavailable, StringComparison.Ordinal))
            {
                return NodeResult.Failure(unavailable, "保存時のパッケージ構成が実行Peerにありません。同じ依存関係を導入するか、意図した構成で再解析してください。", EOutcome.NotExecuted);
            }
            if (!dispatched && exception.Message.StartsWith(runtimePrefix, StringComparison.Ordinal))
            {
                return NodeResult.Failure(exception.Message, "実行先PeerのPythonまたはNode.jsの実行環境を確認してください。既定環境の欠落を別の環境へ置き換えて実行することはありません。", EOutcome.NotExecuted);
            }
            if (!dispatched && string.Equals(exception.Message, unsafeNumber, StringComparison.Ordinal))
            {
                return NodeResult.Failure(unsafeNumber, "JavaScriptで正確に扱えない整数です。±9,007,199,254,740,991を超える整数は文字列で受け渡してください。", EOutcome.NotExecuted);
            }
            EOutcome outcome = EOutcome.NotExecuted;
            if (dispatched && exception is not FileNotFoundException)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("script.operationUnconfirmed", "スクリプト処理の完了を確認できません。外部副作用の取消や再実行は行いません。", outcome);
        }
    }

    /// <summary>保存済み構成が未導入なら、現在版への代替や実行を行いません。</summary>
    /// <returns>この一回の処理で共有する環境です。</returns>
    internal ScriptPackageEnvironment Environment(ScriptAnalysis analysis)
    {
        if (analysis.PackageEnvironmentId is null)
        {
            return ScriptPackageEnvironment.Empty;
        }
        EScriptPackageKind kind = EScriptPackageKind.Nuget;
        if (python)
        {
            kind = EScriptPackageKind.Pip;
        }
        if (javascript)
        {
            kind = EScriptPackageKind.Npm;
        }
        return packages?.Resolve(kind, analysis.PackageEnvironmentId) ?? throw new InvalidOperationException("script.packages.environmentUnavailable");
    }

    /// <summary>個別入力を名前付きJSONへまとめ、入力が持つPathの実行先表記も維持します。</summary>
    /// <returns>選択された一方の方式から構成した引数です。</returns>
    internal static NodeValue Arguments(NodeExecutionContext context, ScriptContract contract)
    {
        if (!ScriptInputPorts.Individual(context.Settings))
        {
            return NodeSupport.Input(context, "arguments") ?? NodeValue.FromObject(new { });
        }
        Dictionary<string, JsonElement> supplied = new(StringComparer.Ordinal);
        foreach (string name in contract.Arguments.Keys)
        {
            if (NodeSupport.Input(context, ScriptInputPorts.Name(name)) is { } value)
            {
                if (value.DataType is not null)
                {
                    value = PathTypeMetadata.Apply(value, value.DataType, OperatingSystem.IsWindows());
                }
                supplied.Add(name, ScriptBinaryValues.Argument(ScriptArguments.Normalize(value, name, contract), contract.Arguments[name]).Json);
            }
        }
        return NodeValue.FromObject(supplied);
    }
}
