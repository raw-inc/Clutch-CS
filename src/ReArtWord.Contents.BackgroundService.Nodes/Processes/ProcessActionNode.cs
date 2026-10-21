using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Processes;

/// <summary>一つの登録プロセス操作を共通ノード結果へ対応付けます。</summary>
internal sealed class ProcessActionNode : IActionNode
{
    private readonly Func<string, NodeExecutionContext, CancellationToken, ValueTask<ProcessOperationResult>> action;

    /// <summary>一つの具体操作とその公開スキーマを固定します。</summary>
    internal ProcessActionNode(string type, string schema, Func<string, NodeExecutionContext, CancellationToken, ValueTask<ProcessOperationResult>> action, bool inputRequired = false)
    {
        this.action = action;
        Descriptor = NodeSupport.Describe(type, type, "Processes", schema, [new PortDescriptor("value", Required: inputRequired) { IsLegacy = !inputRequired }], outputs: [new("value"), new("pid", EPortValueKind.Json) { DataType = new("integer", Optional: true) }], routes: ["success", "failure", "notExecuted", "unknown", "cancelled"]) with { HasAsyncCompletion = true, Location = EExecutionLocation.ExplicitPeer };
    }

    /// <summary>操作の入出力と明示Peer要件です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>他Peerの実行環境に触れず設定を検証します。</summary>
    /// <returns>設定構造の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>実行Peerで登録を解決して一回の操作を行います。</summary>
    /// <returns>実プロセスの操作結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        try
        {
            string id = new NodeSettings(context.Settings).ReadText("processId")!;
            ProcessOperationResult result = await action(id, context, cancellationToken).ConfigureAwait(false);
            return Result(result);
        }
        catch (OperationCanceledException)
        {
            return NodeResult.Failure("process.cancelled", "process操作の待機を中断しました。", EOutcome.Cancelled);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return NodeResult.Failure("process.invalidInput", "process操作に必要な登録指定または入力が不正です。", EOutcome.NotExecuted);
        }
    }

    /// <summary>共通プロセス結果を情報を失わずノードへ写します。</summary>
    /// <returns>失敗時も観測状態を保持した結果です。</returns>
    internal static NodeResult Result(ProcessOperationResult result)
    {
        NodeValue status = NodeValue.FromJson(JsonSerializer.SerializeToElement(result.Status, ContractJson.Options));
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal)
        {
            ["value"] = status,
            ["pid"] = NodeValue.FromObject(result.Status?.Identity?.ProcessId),
        };
        if (result.Outcome == EOutcome.Succeeded)
        {
            return NodeResult.Success(status) with { Outputs = outputs };
        }
        return NodeResult.Failure(result.Reason ?? "process.failed", "登録processの操作を正常完了できませんでした。", result.Outcome) with { Outputs = outputs };
    }
}
