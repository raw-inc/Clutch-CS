using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Processes;

/// <summary>生存だけを準備完了にせず明示条件の成立を待ちます。</summary>
internal sealed class ProcessReadyNode : IActionNode
{
    private readonly IProcessService service;

    /// <summary>共通プロセス状態の取得先を固定します。</summary>
    internal ProcessReadyNode(IProcessService service)
    {
        this.service = service;
    }

    /// <summary>有限共通既定または明示無期限を選べる設定です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("process.waitReady", "準備完了待ち", "Processes", """{"type":"object","required":["processId"],"properties":{"processId":{"type":"string","minLength":1},"timeoutMs":{"type":["integer","null"],"minimum":1,"maximum":86400000}}}""", [new PortDescriptor("value")], outputs: [new("value"), new("pid", EPortValueKind.Json) { DataType = new("integer", Optional: true) }], routes: ["success", "failure", "notExecuted", "unknown", "cancelled"]) with { HasAsyncCompletion = true, Location = EExecutionLocation.ExplicitPeer };

    /// <summary>接続先PCの実環境に触れず設定を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>現在の起動回についてready成立を待ちます。</summary>
    /// <returns>準備完了または有限期限の失敗です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        NodeSettings settings = new(context.Settings);
        string id = settings.ReadText("processId")!;
        Guid? expectedInstance = null;
        string valueKey = "value";
        string idKey = "id";
        string instanceKey = "instanceId";
        if (context.Inputs.TryGetValue(valueKey, out NodeValue? input))
        {
            if (input.Kind != EValueKind.Json || input.Json.ValueKind != JsonValueKind.Object || !input.Json.TryGetProperty(idKey, out JsonElement inputId) || inputId.ValueKind != JsonValueKind.String || !string.Equals(inputId.GetString(), id, StringComparison.Ordinal) || !input.Json.TryGetProperty(instanceKey, out JsonElement instance) || instance.ValueKind != JsonValueKind.String || !instance.TryGetGuid(out Guid parsed) || parsed == Guid.Empty)
            {
                return NodeResult.Failure("process.readyInputMismatch", "前段のprocess識別子と起動回を確認できません。", EOutcome.NotExecuted);
            }
            expectedInstance = parsed;
        }
        int? milliseconds = service.Options.DefaultReadyTimeoutMilliseconds;
        System.Text.Json.JsonElement configured = settings.Get("timeoutMs");
        if (configured.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            milliseconds = null;
        }
        if (configured.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            milliseconds = configured.GetInt32();
        }
        using CancellationTokenSource timeout = new();
        if (milliseconds.HasValue)
        {
            timeout.CancelAfter(milliseconds.Value);
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                ProcessOperationResult status = await service.ObserveAsync(id, linked.Token).ConfigureAwait(false);
                if (status.Outcome != EOutcome.Succeeded)
                {
                    return ProcessActionNode.Result(status);
                }
                if (status.Status is null || status.Status.InstanceId == Guid.Empty)
                {
                    return NodeResult.Failure("process.readyInstanceUnavailable", "準備待ち対象の起動回を確認できません。", EOutcome.NotExecuted);
                }
                expectedInstance ??= status.Status.InstanceId;
                if (expectedInstance.Value != status.Status.InstanceId)
                {
                    return NodeResult.Failure("process.readyInstanceChanged", "準備待ち対象が別の起動回へ変わりました。") with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { expectedInstanceId = expectedInstance, observedInstanceId = status.Status.InstanceId }) } };
                }
                if (status.Status?.Running == true && status.Status.Ready == true)
                {
                    return ProcessActionNode.Result(status);
                }
                await Task.Delay(service.Options.PollMilliseconds, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 待機の期限と利用者取消を以下で分離します。
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("process.readyCancelled", "準備完了待ちを取り消しました。", EOutcome.Cancelled);
        }
        return NodeResult.Failure("process.readyTimeout", "準備条件が有限の待機上限内に成立しませんでした。");
    }
}
