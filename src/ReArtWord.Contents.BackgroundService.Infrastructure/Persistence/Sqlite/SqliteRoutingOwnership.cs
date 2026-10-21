using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>実変更と同じSQLite取引で操作の保存先を固定します。</summary>
internal static class SqliteRoutingOwnership
{
    /// <summary>実変更と所有先を一度の耐久取引へまとめます。</summary>
    /// <returns>既存の比較更新または準備の結果です。</returns>
    internal static DurableReceipt Change(string path, RoutingOwner owner, DurableMutation[] mutations, bool prepare, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteSession session = new(path);
        Guid id = owner.Operation.OperationId;
        RepositoryDocument document = session.Load(id, mutations);
        DurableReceipt result = prepare ? DurableRepository.Prepare(document, id, mutations, owner.Operation.Fingerprint)
                                        : DurableRepository.Apply(document, id, mutations, owner.Operation.Fingerprint);
        Record(session, document, owner);
        session.Save(document);
        session.Commit();
        return result;
    }

    /// <summary>既存所有先との一致を確認し、初回だけ同じ文書へ記録します。</summary>
    internal static void Record(SqliteSession session, RepositoryDocument document, RoutingOwner owner)
    {
        string key = "owner:" + owner.Operation.OperationId.ToString("N"), scope = "peer:persistence-routing";
        string address = DurableRepository.GetAddress(scope, key);
        DurableEntry? old = session.Read<DurableEntry>("entry", address);
        NodeValue value = NodeValue.FromObject(owner);
        if (old?.Value is not null && !string.Equals(ContractJson.Hash(old.Value), ContractJson.Hash(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException("操作の保存所有先が取引前に変更されました。");
        }
        if (old is null)
        {
            document.Entries[address] = new(scope, key, Guid.NewGuid().ToString("N"), value);
        }
    }
}
