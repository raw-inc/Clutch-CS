using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>共通事象を購読し通常の条件ノードへ渡します。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class EventHookNode : ISourceNode
{
    private const string SettingCount = "count";

    private readonly IWorkflowEvents events;

    /// <summary>事象配信境界を設定します。</summary>
    public EventHookNode(IWorkflowEvents events)
    {
        ArgumentNullException.ThrowIfNull(events);
        this.events = events;
    }

    /// <summary>事象名と発生元の任意フィルターを公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.hook", "事象の購読", "Control",
        """{"type":"object","properties":{"name":{"type":"string"},"workflow":{"type":"string"},"node":{"type":"string"},"peer":{"type":"string"},"count":{"type":"integer","minimum":1}}}""",
        [new PortDescriptor("value") { IsLegacy = true }], [new PortDescriptor("value"), new PortDescriptor("event")], kind: ENodeKind.Source) with
    {
        Description = "指定した内部イベントを継続購読し、受信した内容から後続の処理を開始します。",
    };

    /// <summary>購読設定を検証します。</summary>
    /// <returns>不正な設定項目です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSupport.Validate(definition, Descriptor);
    }

    /// <summary>購読開始後の事象を通知します。</summary>
    /// <returns>正常終了と緊急取消を区別した結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        int emitted = 0;
        IAsyncEnumerator<WorkflowEvent> subscription = events.SubscribeAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable subscriptionLifetime = subscription.ConfigureAwait(false);
        try
        {
            Task<bool> reading = subscription.MoveNextAsync().AsTask();
            await context.ReportStatusAsync(new NodeSourceStatus(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            bool available = await reading.ConfigureAwait(false);
            while (available)
            {
                if (context.IsStopRequested)
                {
                    break;
                }
                NodeExecutionContext snapshot = context.BeginItem();
                NodeSettings settings = new(snapshot.Settings);
                WorkflowEvent item = subscription.Current;
                WorkflowHookStep step = new(snapshot.WorkflowId, snapshot.NodeId);
                if (Matches(settings, item) && (item.Hooks?.Count ?? 0) < 32 && item.Hooks?.Contains(step) != true)
                {
                    snapshot = snapshot with { Hooks = [.. item.Hooks ?? [], step] };
                    await context.EmitAsync(snapshot, new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                    {
                        ["value"] = item.Data.Copy(),
                        ["event"] = NodeValue.FromObject(item with { Data = NodeValue.Null }),
                    }), lifetime.Token).ConfigureAwait(false);
                    emitted++;
                    if (settings.ReadInt32(SettingCount, int.MaxValue) <= emitted)
                    {
                        break;
                    }
                }
                available = await subscription.MoveNextAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.IsStopRequested && !cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("hook.cancelled", "事象の購読を取り消しました。", EOutcome.Cancelled);
        }
        return NodeResult.Success();
    }

    /// <summary>指定された発生元の条件だけを照合します。</summary>
    /// <returns>すべての指定条件を満たすか。</returns>
    private static bool Matches(NodeSettings settings, WorkflowEvent item)
    {
        return (settings.ReadText("name") is not string name || string.Equals(name, item.Name, StringComparison.Ordinal))
            && (settings.ReadText("workflow") is not string workflow || string.Equals(workflow, item.WorkflowId, StringComparison.Ordinal))
            && (settings.ReadText("node") is not string node || string.Equals(node, item.NodeId, StringComparison.Ordinal))
            && (settings.ReadText("peer") is not string peer || string.Equals(peer, item.PeerId, StringComparison.Ordinal));
    }
}
