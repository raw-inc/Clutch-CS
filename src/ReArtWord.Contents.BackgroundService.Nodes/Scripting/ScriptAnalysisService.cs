using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.JavaScript;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>言語別解析を一つの編集・保存前検証入口へ接続します。</summary>
public static class ScriptAnalysisService
{
    private static readonly Lock gate = new();
    private static readonly Dictionary<string, ScriptAnalysis> cache = new(StringComparer.Ordinal);

    /// <summary>動的言語のコードは構文木としてのみ読み、型推定のために実行しません。</summary>
    /// <returns>型契約・未確定候補・診断です。</returns>
    public static async Task<ScriptAnalysis> AnalyzeAsync(ScriptAnalysisRequest request, CancellationToken cancellationToken = default)
        => await AnalyzeAsync(request, ScriptPackageEnvironment.Empty, cancellationToken).ConfigureAwait(false);

    /// <summary>同じ固定構成の参照先を解析と実行へ引き渡します。</summary>
    /// <returns>パッケージ構成を含む型契約・診断です。</returns>
    public static async Task<ScriptAnalysis> AnalyzeAsync(ScriptAnalysisRequest request, ScriptPackageEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        string csharp = "csharp";
        if (string.Equals(request.Language, csharp, StringComparison.Ordinal))
        {
            if (request.SyntaxOnly)
            {
                return CSharpScriptCompiler.AnalyzeSyntax(request);
            }
            return CSharpScriptCompiler.Analyze(request.Code, environment, request.Continuous);
        }
        string fingerprint = ContractJson.Hash(request);
        string key = fingerprint + ":" + environment.Info?.Revision + ":" + environment.RuntimeExecutable;
        bool javascript = string.Equals(request.Language, "javascript", StringComparison.Ordinal);
        string python = "python";
        if ((!javascript && !string.Equals(request.Language, python, StringComparison.Ordinal)) || (!request.SyntaxOnly && string.IsNullOrWhiteSpace(request.Code)) || 65536 < request.Code.Length)
        {
            return new(fingerprint, null, ["script.languageOrCodeInvalid"]);
        }
        using (gate.EnterScope())
        {
            if (cache.TryGetValue(key, out ScriptAnalysis? prior))
            {
                return prior;
            }
        }
        ScriptAnalysis result;
        try
        {
            using ScriptWorkspace workspace = new();
            string input = await workspace.WriteAsync(request, 1048576, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string output;
            if (javascript)
            {
                string host = await workspace.WriteJavaScriptHostAsync(timeout.Token).ConfigureAwait(false);
                output = await BoundedProcessRunner.RunAsync(environment.Info?.NodePath ?? environment.RuntimeExecutable ?? request.NodePath ?? string.Empty, [host, "analyze", input],
                    JavaScriptRuntime.Limits(1048576, 268435456), timeout.Token).ConfigureAwait(false);
            }
            else
            {
                string host = await workspace.WritePythonHostAsync(timeout.Token).ConfigureAwait(false);
                output = await BoundedProcessRunner.RunAsync(environment.PythonExecutable ?? environment.RuntimeExecutable ?? request.PythonPath ?? string.Empty, ["-I", "-S", host, "analyze", input], PythonRuntime.Limits(1048576, 268435456), timeout.Token).ConfigureAwait(false);
            }
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            ScriptContract? contract = root.GetProperty("contract").Deserialize<ScriptContract>(ContractJson.Options);
            ScriptContract? suggested = null;
            string suggestion = "suggestedContract";
            if (root.TryGetProperty(suggestion, out JsonElement candidate))
            {
                suggested = candidate.Deserialize<ScriptContract>(ContractJson.Options);
            }
            if (contract is not null)
            {
                Validate(contract);
            }
            result = new(fingerprint, contract, root.GetProperty("diagnostics").Deserialize<string[]>() ?? [], suggested, environment.Info?.Revision);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            string reason = "script.pythonAnalysisUnavailable";
            if (javascript)
            {
                reason = "script.javascriptAnalysisUnavailable";
            }
            return new(fingerprint, null, [reason]);
        }
        string unavailable = "script.javascriptAnalysisUnavailable";
        if (result.Diagnostics.Contains(unavailable, StringComparer.Ordinal))
        {
            return result;
        }
        using (gate.EnterScope())
        {
            if (16 <= cache.Count)
            {
                cache.Remove(cache.Keys.First());
            }
            cache[key] = result;
        }
        return result;
    }

    /// <summary>推定・明示型のどちらもWorkflowEditorの既存検査へ通します。</summary>
    internal static void Validate(ScriptContract contract, bool allowBinary = false)
    {
        if (contract.Arguments is null || 128 < contract.Arguments.Count || contract.Result is null)
        {
            throw new FormatException("script.contractInvalid");
        }
        ScriptContract validated = contract;
        if (allowBinary)
        {
            validated = contract with { Result = ScriptSettings.JsonType(contract.Result), Arguments = contract.Arguments.ToDictionary(pair => pair.Key, pair => ScriptSettings.JsonType(pair.Value), StringComparer.Ordinal) };
        }
        ValueTypeOperations.ValidateType(new("object", Properties: validated.Arguments));
        ValueTypeOperations.ValidateType(validated.Result);
        string array = "array";
        if (contract.VariadicArgument is { } variadic && (!contract.Arguments.TryGetValue(variadic, out Contracts.Values.ValueTypeDefinition? type)
            || !string.Equals(type.Type, array, StringComparison.Ordinal) || !type.Optional))
        {
            throw new FormatException("script.contractInvalid");
        }
    }
}
