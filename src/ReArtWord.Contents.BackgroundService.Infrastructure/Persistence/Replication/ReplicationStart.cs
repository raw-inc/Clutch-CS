using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>固定参加者の記録とローカル準備を、送信前に耐久保存します。</summary>
internal sealed class ReplicationStart
{
    /// <summary>送信前に変更候補と開始記録の所有権を取得します。</summary>
    private ReplicationStart(Guid operationId, IReadOnlyList<DurableMutation> mutations, IReadOnlyList<DurableMutation> initialization)
    {
        if (operationId == Guid.Empty || mutations.Count == 0 || initialization.Count == 0 || initialization.Any(item => DurableRepository.IsShared(item.Scope)))
        {
            throw new ArgumentException("操作ID、共有変更、ローカル開始記録が必要です。", nameof(operationId));
        }
        OperationId = operationId;
        Mutations = DurableMutationOwnership.Copy(mutations);
        Initialization = DurableMutationOwnership.Copy(initialization);
        Fingerprint = RepositoryCausality.Fingerprint(Mutations);
        InitializationFingerprint = RepositoryCausality.Fingerprint(Initialization);
    }

    /// <summary>複製の元操作IDです。</summary>
    internal Guid OperationId
    {
        get;
    }

    /// <summary>開始記録自体の従来形式の操作IDです。</summary>
    internal Guid InitializationId
    {
        get;
    } = Guid.CreateVersion7();

    /// <summary>非同期境界の前に所有した変更候補です。</summary>
    internal DurableMutation[] Mutations
    {
        get;
    }

    /// <summary>共有対象外の固定参加者と処理中の結果です。</summary>
    internal DurableMutation[] Initialization
    {
        get;
    }

    /// <summary>因果情報を含めない元変更の指紋です。</summary>
    internal string Fingerprint
    {
        get;
    }

    /// <summary>開始記録の比較更新に用いる指紋です。</summary>
    internal string InitializationFingerprint
    {
        get;
    }

    /// <summary>同じ媒体なら一取引、対応しない構成なら従来の順序で保存します。</summary>
    /// <returns>開始記録、準備結果、全Peerへ渡す同じ因果情報付き変更です。</returns>
    internal static async ValueTask<ReplicationStartResult> BeginAsync(IDurableRepository repository, Guid operationId,
        IReadOnlyList<DurableMutation> mutations, IReadOnlyList<DurableMutation> initialization, CancellationToken cancellationToken = default)
    {
        ReplicationStart start = new(operationId, mutations, initialization);
        if (repository is IReplicationTransactionStore store && await store.TryStartAsync(start, cancellationToken).ConfigureAwait(false) is { } atomic)
        {
            return atomic;
        }
        DurableReceipt initialized = await repository.ApplyBatchAsync(start.InitializationId, start.Initialization, cancellationToken).ConfigureAwait(false);
        if (initialized.Outcome != EOutcome.Succeeded)
        {
            return new(initialized, null, []);
        }
        IReadOnlyList<DurableMutation> stamped = await repository.StampAsync(start.Mutations, cancellationToken).ConfigureAwait(false);

        // 未対応媒体の準備は通常の全Peer並行処理に残し、ローカル待機で遠隔準備を止めません。
        return new(initialized, null, stamped);
    }
}
