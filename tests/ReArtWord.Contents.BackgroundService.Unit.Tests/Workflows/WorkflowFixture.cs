using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実行ランタイムをノードの実行処理へ接続するローカル試験用の構成です。</summary>
internal sealed class WorkflowFixture : IAsyncDisposable
{
    /// <summary>試験が必要とするノードだけを登録します。</summary>
    internal WorkflowFixture(params INode[] nodes)
    {
        Registry = new NodeRegistry(nodes);
        Compiler = new WorkflowCompiler(Registry);
        Engine = new WorkflowEngine(Registry, Peers, Dispatcher);
        Safety = new NodeSafetyGate(Peers);
        Sources = new SourceSupervisor(Registry, Peers, Engine, Safety);
        Executor = new NodeExecutor(Registry, Peers, Sources, Safety);
        Dispatcher.Handler = Executor.ExecuteAsync;
    }

    /// <summary>ノード登録です。</summary>
    internal NodeRegistry Registry
    {
        get;
    }

    /// <summary>実行前の定義検証です。</summary>
    internal WorkflowCompiler Compiler
    {
        get;
    }

    /// <summary>実際のグラフ実行です。</summary>
    internal WorkflowEngine Engine
    {
        get;
    }

    /// <summary>実待受の管理です。</summary>
    internal SourceSupervisor Sources
    {
        get;
    }

    /// <summary>有限実行の境界です。</summary>
    internal NodeExecutor Executor
    {
        get;
    }

    /// <summary>安全停止の境界です。</summary>
    internal NodeSafetyGate Safety
    {
        get;
    }

    /// <summary>参加状態を変更できる試験用の参照元です。</summary>
    internal TestPeers Peers
    {
        get;
    } = new();

    /// <summary>配送の結果だけを試験で指定できる境界です。</summary>
    internal TestDispatcher Dispatcher
    {
        get;
    } = new();

    /// <summary>入力のない有限試験ノードです。</summary>
    /// <returns>最小の定義です。</returns>
    internal static NodeDefinition Definition(string id, string type = "test.action", string settings = "{}") => new(id, type, NodeSettings.Schema(settings));

    /// <summary>開始した実待受と一件処理を終了します。</summary>
    /// <returns>所有している実行の終了です。</returns>
    public ValueTask DisposeAsync() => ((IAsyncDisposable)Sources).DisposeAsync();

    /// <summary>優先度を変えて対象解決を検証するPeer一覧です。</summary>
    internal sealed class TestPeers : IPeerDirectory
    {
        /// <summary>ローカルの永続識別子です。</summary>
        public string LocalPeerId => "a";

        /// <summary>ローカルプロセス実体です。</summary>
        public Guid LocalInstanceId
        {
            get;
        } = Guid.NewGuid();

        /// <summary>CoordinatorはローカルPeerです。</summary>
        public string? CoordinatorId => "a";

        /// <summary>追加対象です。</summary>
        internal IReadOnlyList<PeerInfo> Others
        {
            get; set;
        } = [];

        /// <summary>ローカルPeerの参加状態。通常参加と限定参加の境界を試験します。</summary>
        internal EPeerStatus Status
        {
            get; set;
        } = EPeerStatus.Ready;

        /// <summary>試験の時点の参加状態です。</summary>
        /// <returns>ローカルと追加対象です。</returns>
        public IReadOnlyList<PeerInfo> GetPeers() => [new PeerInfo("a", LocalInstanceId, "host-a", "127.0.0.1", 0, Status, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow), .. Others];

        /// <summary>試験ではGroupを使いません。</summary>
        /// <returns>空の対応です。</returns>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetHostGroups() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    /// <summary>通信以外の実ランタイムへ要求を渡します。</summary>
    internal sealed class TestDispatcher : IRemoteNodeDispatcher
    {
        /// <summary>ノード実行または意図した通信結果です。</summary>
        internal Func<RemoteExecutionRequest, CancellationToken, Task<NodeResult>>? Handler
        {
            get; set;
        }

        /// <summary>指定実体への要求を観測します。</summary>
        /// <returns>試験対象の結果です。</returns>
        public Task<NodeResult> ExecuteAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default) => Handler!(request, cancellationToken);
    }

    /// <summary>グラフから実際に渡された文脈を観測します。</summary>
    internal sealed class ActionNode : IActionNode
    {
        private readonly Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> handler;

        /// <summary>処理内容と必須入力を設定します。</summary>
        internal ActionNode(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> handler, bool required = false)
        {
            this.handler = handler;
            Descriptor = NodeSupport.Describe("test.action", "観測", "Test", "{}", [new PortDescriptor("value", Required: required)], routes: ["true", "false"]) with { HasAsyncCompletion = true };
        }

        /// <summary>設定を制約しない試験ノードです。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        }

        /// <summary>試験固有の追加検証はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>配送された文脈を試験へ渡します。</summary>
        /// <returns>試験側で指定した観測可能な結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => handler(context, cancellationToken);
    }
}
