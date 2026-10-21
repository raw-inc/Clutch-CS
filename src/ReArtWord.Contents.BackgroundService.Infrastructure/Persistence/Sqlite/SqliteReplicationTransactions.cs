using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>送信前の開始または確定判断を、元変更と同じSQLite取引で保存します。</summary>
internal static class SqliteReplicationTransactions
{
    /// <summary>確定判断の保存で通常コミットのflushを増やさず、途中例外では両方を戻します。</summary>
    /// <returns>耐久保存済みの局所結果です。</returns>
    internal static DurableReceipt Commit(string path, ReplicationCommit commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        RepositoryDocument document = session.Load(commit.OperationId, [commit.Decision]);
        DurableReceipt result = commit.Apply(document);
        session.Save(document);
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return result;
    }

    /// <summary>既存の比較条件と保存形式を保ち、途中例外では全てをロールバックします。</summary>
    /// <returns>全書込みを確定してから返す、開始と準備の結果です。</returns>
    internal static ReplicationStartResult Write(string path, ReplicationStart start, RoutingOwner? initializationOwner = null,
        RoutingOwner? preparationOwner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        RepositoryDocument document = session.Load(start.OperationId, [.. start.Mutations, .. start.Initialization]);
        DurableReceipt initialized = DurableRepository.Apply(document, start.InitializationId, start.Initialization, start.InitializationFingerprint);
        if (initializationOwner is not null)
        {
            SqliteRoutingOwnership.Record(session, document, initializationOwner);
        }
        DurableReceipt? prepared = null;
        IReadOnlyList<DurableMutation> stamped = [];
        if (initialized.Outcome == EOutcome.Succeeded)
        {
            stamped = RepositoryCausality.Stamp(document, start.Mutations);
            prepared = DurableRepository.Prepare(document, start.OperationId, stamped, start.Fingerprint);
            if (preparationOwner is not null)
            {
                SqliteRoutingOwnership.Record(session, document, preparationOwner);
            }
        }
        session.Save(document);
        cancellationToken.ThrowIfCancellationRequested();
        session.Commit();
        return new(initialized, prepared, stamped);
    }
}
