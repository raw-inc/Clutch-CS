using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>実SQLite・UDP・許可ルート内のファイルを持つ試験Peerです。</summary>
internal sealed class FileTestPeer : IAsyncDisposable
{
    /// <summary>試験ごとに独立したファイル所有領域を作ります。</summary>
    public FileTestPeer(string clusterId, string peerId, int port, bool fresh, Func<INode, INode>? wrap = null)
    {
        Root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Repository = new SqliteRepository(Path.Combine(Root, "state.db"));
        Cluster = new PeerCluster(new ClusterOptions(clusterId, peerId, peerId, "127.0.0.1", "127.255.255.255", port, 1, fresh, Root) { HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = TimeSpan.FromSeconds(2) }, Repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        FileSystemOptions options = new([Root], RecoveryDirectory: Path.Combine(Root, "recovery"));
        Registry = new NodeRegistry(FileSystemNodeRegistration.Create(options).Select(node => wrap?.Invoke(node) ?? node));
        Transactions = new FileTransactionService(Registry, Cluster, Repository, options);
        Cluster.RemoteExecutionHandler = (request, token) => Transactions.ExecuteFileAsync((IActionNode)Registry.Find(request.Node.Type)!, Context(request), token).AsTask();
    }

    /// <summary>許可された作業領域です。</summary>
    public string Root
    {
        get;
    }

    /// <summary>永続保存先です。</summary>
    public SqliteRepository Repository
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
    }
}
