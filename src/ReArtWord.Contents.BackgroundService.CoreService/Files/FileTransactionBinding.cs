using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files;

/// <summary>EngineとローカルNodeExecutorへ同じファイルtransaction境界を渡します。</summary>
internal sealed class FileTransactionBinding : IDisposable
{
    private readonly IWorkflowLog log;
    private readonly string peerId;

    /// <summary>一Peerに一つの全対象補償と重複調停を接続します。</summary>
    public FileTransactionBinding(NodeRegistry registry, PeerCluster cluster, IDurableRepository repository, FileSystemOptions options, Nodes.FileSystem.Transfers.FileTransferService files, IWorkflowLog log)
    {
        this.log = log;
        peerId = cluster.LocalPeerId;
        Files = files;
        Transactions = new FileTransactionService(registry, cluster, repository, options);
    }

    /// <summary>GUIとノードで共有する転送・許可・排他の実装です。</summary>
    internal Nodes.FileSystem.Transfers.FileTransferService Files
    {
        get;
    }

    /// <summary>Engineにはdispatcher、NodeExecutorにはExecuteFileAsyncを接続します。</summary>
    public FileTransactionService Transactions
    {
        get;
    }

    /// <summary>停止時は互換保存を有限時間で試み、確認不能でも通信・実処理の停止を続けます。</summary>
    /// <returns>予約を消さず、旧版復帰の確認が必要な場合は一回だけ記録します。</returns>
    internal async Task PrepareStopAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await Transactions.PrepareStopAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or System.Data.Common.DbException or System.Text.Json.JsonException)
        {
            await WorkflowLogBoundary.WriteAsync(log, new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "WARN", peerId,
                "File.ReservationCompatibilityUnconfirmed"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>予約管理の同期資源を解放します。未知の予約は削除しません。</summary>
    public void Dispose() => Transactions.Dispose();
}
