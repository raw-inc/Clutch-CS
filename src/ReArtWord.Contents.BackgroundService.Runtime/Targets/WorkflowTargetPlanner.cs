using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>一件の開始前に、従来の指定とPC選択を同じ参加情報で解決します。</summary>
public sealed class WorkflowTargetPlanner
{
    private readonly NodeRegistry registry;
    private readonly IPeerDirectory directory;
    private readonly TimeProvider clock;

    /// <summary>ノードの契約、参加情報、選択時刻の取得元を指定します。</summary>
    public WorkflowTargetPlanner(NodeRegistry registry, IPeerDirectory directory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(clock);
        this.registry = registry;
        this.directory = directory;
        this.clock = clock;
    }

    /// <summary>新たな採取を行わず、受信・採取済みのリソース観測を読み取ります。</summary>
    public Func<IReadOnlyDictionary<string, ResourceSample>>? ResourceObservations
    {
        get; set;
    }

    /// <summary>継続入力が負荷変動だけで移動しないよう、共有済みの担当を取得します。</summary>
    public Func<string, string, TargetResolution?>? SourceTargets
    {
        get; set;
    }

    /// <summary>設定版に含まれる全ノードの実行対象を固定します。</summary>
    /// <returns>対象実体、選択根拠、開始できない場合の理由。</returns>
    public IReadOnlyDictionary<string, TargetResolution> Capture(CompiledWorkflow workflow, PeerInfo? sourcePeer, ClusterOperationalSnapshot? operational)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        DirectorySnapshot snapshot = new(directory, operational?.Settings.HostGroups);
        TargetResolver legacy = new(snapshot);
        PcSelectionResolver pcs = new(snapshot.GetPeers(), ResourceObservations?.Invoke() ?? new Dictionary<string, ResourceSample>(StringComparer.Ordinal), clock.GetUtcNow(), snapshot.LocalInstanceId);
        Dictionary<string, TargetResolution> targets = workflow.OrderedNodes.ToDictionary(node => node.Id, node =>
        {
            if (workflow.PcSelections?.TryGetValue(node.Id, out IReadOnlyList<PcSelection>? choices) == true)
            {
                if (registry.Find(node.Type) is ISourceNode)
                {
                    return SourceTargets?.Invoke(workflow.Definition.Id, node.Id) ?? new TargetResolution(EExecutionScope.One, [],
                        NodeResult.Failure("selection.sourcePending", "待受の実行PCを共有確認中です。", EOutcome.NotExecuted));
                }
                return pcs.Resolve(choices, node.PcExecution);
            }
            return legacy.Resolve(node, registry.Find(node.Type)!.Descriptor, sourcePeer, operational?.Settings.HostGroups, operational);
        }, StringComparer.Ordinal);
        return new WorkflowTargetDictionary(targets, pcs);
    }

    /// <summary>選択途中の参加情報の変更を、同じ一件の別ノードへ持ち込みません。</summary>
    private sealed class DirectorySnapshot : IPeerDirectory
    {
        private readonly PeerInfo[] peers;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> groups;

        /// <summary>ローカル実体、担当、参加集合、Groupを一度取得します。</summary>
        internal DirectorySnapshot(IPeerDirectory source, IReadOnlyDictionary<string, IReadOnlyList<string>>? hostGroups)
        {
            LocalPeerId = source.LocalPeerId;
            LocalInstanceId = source.LocalInstanceId;
            CoordinatorId = source.CoordinatorId;
            peers = source.GetPeers().ToArray();
            groups = hostGroups ?? source.GetHostGroups();
        }

        /// <summary>この計画を作成するPeer。</summary>
        public string LocalPeerId
        {
            get;
        }

        /// <summary>計画作成元のプロセス実体。</summary>
        public Guid LocalInstanceId
        {
            get;
        }

        /// <summary>取得時のCoordinator。</summary>
        public string? CoordinatorId
        {
            get;
        }

        /// <summary>同じ計画の全ノードで使用する参加集合を返します。</summary>
        /// <returns>取得済みのPeer一覧。</returns>
        public IReadOnlyList<PeerInfo> GetPeers() => peers;

        /// <summary>同じ計画の全ノードで使用するGroupを返します。</summary>
        /// <returns>取得済みのGroup所属。</returns>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups() => groups;
    }
}
