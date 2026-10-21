using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Resources;

namespace ReArtWord.Contents.BackgroundService.Nodes.Resources;

/// <summary>共通履歴へ保存するリソース観測を後続ワークフローへ継続渡します。</summary>
internal sealed class ResourceWatchNode : ISourceNode
{
    private readonly IResourceService service;

    /// <summary>Host共通の取得先を共有します。</summary>
    internal ResourceWatchNode(IResourceService service)
    {
        this.service = service;
    }

    /// <summary>ワークフローへの供給間隔を設定します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("resource.watch", "resource監視", "Resources", """{"type":"object","properties":{"intervalMs":{"type":"integer","minimum":1,"maximum":3600000,"default":5000}}}""", [new PortDescriptor("value") { IsLegacy = true }], [new PortDescriptor("value"), new PortDescriptor("metrics")], kind: ENodeKind.Source) with { Location = EExecutionLocation.ExplicitPeer };

    /// <summary>設定構造だけを確認します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>取得開始時の設定世代を固定して一件ずつ通知します。</summary>
    /// <returns>監視の正常終了または緊急取消です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        try
        {
            await context.ReportStatusAsync(new NodeSourceStatus(ESourceState.Listening), linked.Token).ConfigureAwait(false);
            while (!context.IsStopRequested && !linked.IsCancellationRequested)
            {
                NodeExecutionContext item = context.BeginItem();
                ResourceSample sample = await service.CaptureAsync(linked.Token).ConfigureAwait(false);
                await context.EmitAsync(item, ResourceObserveNode.Result(sample), linked.Token).ConfigureAwait(false);
                int interval = new NodeSettings(item.Settings).ReadInt32("intervalMs", 5000);
                await Task.Delay(interval, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 予定正常終了と緊急取消を末尾で分離します。
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("resource.watchCancelled", "resource監視を取り消しました。", EOutcome.Cancelled);
        }
        return NodeResult.Success();
    }
}
