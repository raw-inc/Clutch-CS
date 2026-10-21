using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>固定条件と回復段階を元参加集合へ永続保存します。</summary>
internal sealed class FileTransactionJournal(IDurableRepository repository, PeerCluster cluster)
{
    private static string SharedScope => "file:transactions";
    private string LocalScope => "peer:" + cluster.LocalPeerId + ":file-transactions";

    /// <summary>別実体による同ID開始を上書きせず一度だけ受け付けます。</summary>
    /// <returns>初回記録の永続確認。</returns>
    public Task BeginAsync(FileTransactionOperation operation, CancellationToken cancellationToken = default) => SaveCoreAsync(operation, true, cancellationToken);

    /// <summary>元の確認集合を縮めず現在段階を更新します。</summary>
    /// <returns>段階記録の永続確認。</returns>
    public Task SaveAsync(FileTransactionOperation operation, CancellationToken cancellationToken = default) => SaveCoreAsync(operation, false, cancellationToken);

    /// <summary>初回作成と段階更新を原子的に比較保存します。</summary>
    /// <returns>保存確認。</returns>
    private async Task SaveCoreAsync(FileTransactionOperation operation, bool create, CancellationToken cancellationToken = default)
    {
        string key = operation.Request.OperationId.ToString("N");
        bool local = operation.Members.Count == 1 && operation.Members[0].Status == EPeerStatus.Detached;
        string scope = SharedScope;
        if (local)
        {
            scope = LocalScope;
        }
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        string? version = entry?.Version;
        Guid id = Guid.NewGuid();
        if (create)
        {
            version = null;
            id = FileStageClient.Identity(operation.Request.OperationId, "start");
        }
        DurableMutation[] mutations = [new(scope, key, version, NodeValue.FromObject(operation))];
        DurableReceipt result;
        if (local)
        {
            result = await repository.ApplyBatchAsync(id, mutations, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await cluster.ReplicateFixedAsync(id, mutations, operation.Members, cancellationToken).ConfigureAwait(false);
        }
        if (result.Outcome != EOutcome.Succeeded)
        {
            throw new IOException("ファイル操作記録の共有を確認できません。");
        }
    }

    /// <summary>離脱ローカル操作を共有状態へ混ぜず、元の記録を読みます。</summary>
    /// <returns>元の固定操作と結果。</returns>
    public async Task<FileTransactionOperation?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(SharedScope, id.ToString("N"), cancellationToken).ConfigureAwait(false) ?? await repository.ReadAsync(LocalScope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<FileTransactionOperation>();
    }
}
