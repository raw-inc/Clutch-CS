using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>Sourceの所属判定を取得した共通運用版へ揃えます。</summary>
internal sealed class SourceSelection(IPeerDirectory peers, Func<IClusterOperationalProvider?> provider, Func<string, CompiledWorkflow?> workflow, WorkflowTargetPlanner planner)
{
    private readonly TargetResolver resolver = new(peers);

    /// <summary>再計時用の信号配線は、監視自体の自動開始を妨げません。</summary>
    /// <returns>設定値を解決済みの自動待受かです。</returns>
    internal static bool Automatic(CompiledWorkflow plan, NodeDefinition node, INode? implementation)
        => node.StartOnLoad && implementation is ISourceNode && (plan.Incoming[node.Id].Count == 0
            || plan.ConfigurationInputs?.ContainsKey(node.Id) == true
            || implementation is IResettableSourceNode reset && plan.Incoming[node.Id].All(wire => reset.ResetInputs.Contains(wire.ToPort)));

    /// <summary>表示情報や停止方針だけの編集では待受を再接続しないための比較値です。</summary>
    /// <returns>接続に関係する定義の内容識別子。</returns>
    internal static string ConnectionVersion(NodeDefinition node) => Contracts.Serialization.ContractJson.Hash(node with { Position = null, Size = null, LogicalName = null, Safety = null, StartOnLoad = false,
        ContractVersion = NodeBehaviorContracts.CanonicalVersion(node) });

    /// <summary>その判定に使用する共通版を一度だけ取得します。</summary>
    /// <returns>所有コピーまたは未構成時の指定済み版です。</returns>
    public ClusterOperationalSnapshot? Capture(ClusterOperationalSnapshot? fallback = null)
    {
        IClusterOperationalProvider? current = provider();
        if (current is null)
        {
            return fallback;
        }
        return current.Capture();
    }

    /// <summary>必要な共通版を確認できたかを示します。</summary>
    /// <returns>選択判定が可能か。</returns>
    public bool Available(ClusterOperationalSnapshot? snapshot) => provider() is null || snapshot is not null;

    /// <summary>ディレクトリの古いGroup キャッシュを使わず指定版の対象集合を確認します。</summary>
    /// <returns>この実体が対象集合に含まれるか。</returns>
    public bool Contains(NodeDefinition node, NodeDescriptor descriptor, ClusterOperationalSnapshot? snapshot, PeerInfo? sourcePeer = null, string? workflowId = null)
    {
        PeerInfo? local = peers.GetPeers().FirstOrDefault(peer => peer.InstanceId == peers.LocalInstanceId);
        if (!Available(snapshot) || local is null || (!PeerStatusRules.CanParticipate(local.Status) && local.Status != EPeerStatus.Detached))
        {
            return false;
        }
        if (workflowId is not null && workflow(workflowId)?.PcSelections?.ContainsKey(node.Id) == true)
        {
            TargetResolution? assigned = planner.SourceTargets?.Invoke(workflowId, node.Id);
            return assigned?.Error is null && assigned?.Peers.Any(peer => peer.InstanceId == local.InstanceId) == true;
        }
        if (node.Target is { Scope: EExecutionScope.All, Groups: not null } target)
        {
            IReadOnlyDictionary<string, IReadOnlyList<string>> groups = snapshot?.Settings.HostGroups ?? peers.GetHostGroups();
            bool included = target.Peers?.Contains(local.PeerId, StringComparer.Ordinal) == true;
            foreach (string group in target.Groups)
            {
                if (!groups.TryGetValue(group, out IReadOnlyList<string>? hosts))
                {
                    return false;
                }
                included |= hosts.Contains(local.HostId, StringComparer.Ordinal);
            }
            return included;
        }
        return resolver.Resolve(node, descriptor, sourcePeer, snapshot?.Settings.HostGroups, snapshot).Peers.Any(peer => peer.InstanceId == peers.LocalInstanceId);
    }
}
