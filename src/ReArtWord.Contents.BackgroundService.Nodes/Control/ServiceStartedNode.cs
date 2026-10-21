using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>Peer実体ごとに一度だけ、Ready後の起動処理の契機を渡します。</summary>
public sealed class ServiceStartedNode : ISourceNode
{
    private readonly Guid instanceId;
    private readonly DateTimeOffset startedAt;
    private readonly ConcurrentDictionary<(string Workflow, string Node), byte> emitted = new();

    /// <summary>プロセス起動時の実体と時刻を固定します。</summary>
    public ServiceStartedNode(Guid instanceId, DateTimeOffset startedAt)
    {
        this.instanceId = instanceId;
        this.startedAt = startedAt;
    }

    /// <summary>各Peerの起動を通常の配線で利用するカタログです。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("service.started", "Peer の起動", "Control",
        """{"type":"object","properties":{}}""", [], kind: ENodeKind.Source) with
    {
        Location = EExecutionLocation.ExplicitPeer,
        Description = "このPeer実体で初めて待受を開始したときに一度通知します。設定の再適用では通知を繰り返しません。後続をSourcePeerへ配線すると起動したPeer自身で実行できます。",
    };

    /// <summary>固有の設定を持たないSourceを検証します。</summary>
    /// <returns>設定の問題一覧です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>同じ定義の再待受でも起動済みの副作用を暗黙再実行しません。</summary>
    /// <returns>起動入力の受付結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.IsStopRequested || !emitted.TryAdd((context.Initial.WorkflowId, context.Initial.NodeId), 0))
        {
            return NodeResult.Success();
        }
        NodeExecutionContext snapshot = context.BeginItem();
        await context.EmitAsync(snapshot, NodeResult.Success(NodeValue.FromObject(new { peerId = snapshot.PeerId, instanceId, startedAt })), cancellationToken).ConfigureAwait(false);
        return NodeResult.Success();
    }
}
