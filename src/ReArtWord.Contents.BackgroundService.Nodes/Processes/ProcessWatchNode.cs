using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Processes;

/// <summary>登録プロセスの状態変化と明示管理対象の復旧を継続観測します。</summary>
internal sealed class ProcessWatchNode : ISourceNode
{
    private readonly IProcessService service;
    private readonly IWorkflowEvents events;

    /// <summary>共通のプロセス状態とHook出力先を指定します。</summary>
    internal ProcessWatchNode(IProcessService service, IWorkflowEvents events)
    {
        this.service = service;
        this.events = events;
    }

    /// <summary>監視頻度と明示的な自動復旧選択です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("process.watch", "process監視", "Processes", """{"type":"object","required":["processId"],"properties":{"processId":{"type":"string","minLength":1},"autoRecover":{"type":"boolean","default":true},"intervalMs":{"type":"integer","minimum":1,"maximum":60000}}}""", [new PortDescriptor("value") { IsLegacy = true }], [new PortDescriptor("value"), new PortDescriptor("recovery"), new("pid", EPortValueKind.Json) { DataType = new("integer", Optional: true) }], routes: ["success", "failure", "notExecuted", "unknown", "cancelled"], kind: ENodeKind.Source) with { Location = EExecutionLocation.ExplicitPeer };

    /// <summary>他Peerに保存された登録の照会を実行前検証へ持ち込みません。</summary>
    /// <returns>構造上の設定問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>監視自体を継続し、状態変化を独立した一件として送ります。</summary>
    /// <returns>監視の正常停止と緊急取消を分けた終了結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        string? previous = null;
        try
        {
            await context.ReportStatusAsync(new NodeSourceStatus(ESourceState.Listening), linked.Token).ConfigureAwait(false);
            while (!context.IsStopRequested && !linked.IsCancellationRequested)
            {
                NodeExecutionContext item = context.BeginItem();
                NodeSettings settings = new(item.Settings);
                string id = settings.ReadText("processId")!;
                ProcessOperationResult observed = await service.ObserveAsync(id, linked.Token).ConfigureAwait(false);
                ProcessOperationResult? recovery = null;
                bool autoRecover = settings.ReadBoolean("autoRecover", true);
                if (observed.Status?.Mode == EProcessMode.Managed && autoRecover)
                {
                    recovery = await service.RecoverAsync(id, linked.Token).ConfigureAwait(false);
                    if (recovery.Status is not null)
                    {
                        observed = new ProcessOperationResult(EOutcome.Succeeded, recovery.Status, recovery.Reason);
                    }
                }
                string current = ContractJson.Hash(new { observed, recovery });
                if (!string.Equals(previous, current, StringComparison.Ordinal))
                {
                    NodeResult emission = ProcessActionNode.Result(observed);
                    Dictionary<string, NodeValue> outputs = new(emission.Outputs, StringComparer.Ordinal) { ["recovery"] = NodeValue.FromJson(JsonSerializer.SerializeToElement(recovery, ContractJson.Options)) };
                    await context.EmitAsync(item, emission with { Outputs = outputs }, linked.Token).ConfigureAwait(false);
                    NodeValue data = NodeValue.FromJson(JsonSerializer.SerializeToElement(new { observed, recovery }, ContractJson.Options));
                    await events.PublishAsync(new WorkflowEvent(Guid.NewGuid(), "process.changed", item.TimeProvider.GetUtcNow(), item.PeerId, data, item.WorkflowId, item.NodeId, item.CausationId, Hooks: item.Hooks), linked.Token).ConfigureAwait(false);
                    previous = current;
                }
                await Task.Delay(settings.ReadInt32("intervalMs", service.Options.PollMilliseconds), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 予定正常停止と緊急取消を末尾で判定します。
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("process.watchCancelled", "process監視を取り消しました。", EOutcome.Cancelled);
        }
        return NodeResult.Success();
    }
}
