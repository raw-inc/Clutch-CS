using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>実SQLite・UDP・許可ルート内のファイルを持つ試験Peerです。</summary>
internal sealed class FileTestPeer : IAsyncDisposable
{
    private readonly RepositoryRuntime runtime;

    /// <summary>試験の終了まで保存先を所有し、Coreと同じ接続寿命を使用します。</summary>
    private FileTestPeer(RepositoryRuntime runtime, string root, string clusterId, string peerId, int port, bool fresh, Func<INode, INode>? wrap)
    {
        this.runtime = runtime;
        Root = root;
        Repository = runtime.Repository;
        Cluster = new PeerCluster(new ClusterOptions(clusterId, peerId, peerId, "127.0.0.1", "127.255.255.255", port, 1, fresh, Root) { HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromSeconds(2) }, Repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        FileSystemOptions options = new([Root], RecoveryDirectory: Path.Combine(Root, "recovery"));
        Registry = new NodeRegistry(FileSystemNodeRegistration.Create(options).Select(node => wrap?.Invoke(node) ?? node));
        Transactions = new FileTransactionService(Registry, Cluster, Repository, options);
        Cluster.RemoteExecutionHandler = (request, token) => Transactions.ExecuteFileAsync((IActionNode)Registry.Find(request.Node.Type)!, Context(request), token).AsTask();
    }

    /// <summary>独立した保存先でWALと接続の所有期間を設定します。</summary>
    /// <returns>終了時に保存先も解放する試験Peerです。</returns>
    // 抑制理由: 成功時は試験Peerへ所有権を渡し、構築失敗時はfinallyで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "返却する試験Peerが保存先を所有し、構築失敗時だけfinallyで破棄します。")]
    internal static async Task<FileTestPeer> CreateAsync(string clusterId, string peerId, int port, bool fresh, Func<INode, INode>? wrap = null, CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(root, peerId, new(), cancellationToken).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            FileTestPeer peer = new(runtime, root, clusterId, peerId, port, fresh, wrap);
            transferred = true;
            return peer;
        }
        finally
        {
            using RepositoryRuntime? abandoned = transferred ? null
                                                             : runtime;
        }
    }

    /// <summary>許可された作業領域です。</summary>
    public string Root
    {
        get;
    }

    /// <summary>永続保存先です。</summary>
    public IDurableRepository Repository
    {
        get;
    }

    /// <summary>実UDP参加管理です。</summary>
    public PeerCluster Cluster
    {
        get;
    }

    /// <summary>ファイル操作に対応するノードです。</summary>
    public NodeRegistry Registry
    {
        get;
    }

    /// <summary>予約と全対象補償です。</summary>
    public FileTransactionService Transactions
    {
        get;
    }

    /// <summary>元の実行IDをそのままノードへ渡します。</summary>
    /// <returns>実行文脈。</returns>
    public static NodeExecutionContext Context(RemoteExecutionRequest request) => new(request.Id, request.CausationId, request.WorkflowId, request.Revision, request.Node.Id, request.TargetPeerId, request.Node.Settings, request.Inputs, TimeProvider.System, TimeZoneInfo.Utc) { Operational = request.Operational };

    /// <summary>実タスク終了後に調停資源を解放します。</summary>
    /// <returns>試験Peerの終了。</returns>
    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)Cluster).DisposeAsync().ConfigureAwait(false);
        Transactions.Dispose();
        runtime.Dispose();
    }
}
