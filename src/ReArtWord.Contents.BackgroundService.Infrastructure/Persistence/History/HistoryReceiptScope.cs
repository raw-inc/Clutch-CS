using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>空の失敗結果でも、ローカル操作を共有された記録へ変えません。</summary>
internal static class HistoryReceiptScope
{
    /// <summary>既存の論理所有先を、本文を捨てる前に確認します。</summary>
    /// <returns>共有用途だけの操作かです。</returns>
    internal static bool Shared(SqliteSession session, DurableReceipt receipt)
    {
        DurableEntry? entry = session.Read<DurableEntry>("entry", OwnerAddress(receipt.OperationId));
        return Shared(receipt, entry);
    }

    /// <summary>JSONの冷たい所有先も同じ規則で照合します。</summary>
    /// <returns>共有用途だけの操作かです。</returns>
    internal static bool Shared(RepositoryDocument document, DurableReceipt receipt)
    {
        string address = OwnerAddress(receipt.OperationId);
        DurableEntry? owner = document.Entries.GetValueOrDefault(address) ?? JsonHistoryAccess.Read<DurableEntry>(document, "entry", address);
        return Shared(receipt, owner);
    }

    /// <summary>所有先がある空結果は、その確定済み範囲に従います。</summary>
    /// <returns>既存の共有判定です。</returns>
    private static bool Shared(DurableReceipt receipt, DurableEntry? entry)
    {
        RoutingOwner? owner = entry?.Value?.Json.Deserialize<RoutingOwner>(ContractJson.Options);
        if (owner is not null)
        {
            return owner.Operation.Scopes.All(DurableRepository.IsShared);
        }
        return receipt.Entries.All(value => DurableRepository.IsShared(value.Scope));
    }

    /// <summary>保存所有先の固定キーです。</summary>
    /// <returns>リポジトリ内の一意のアドレスです。</returns>
    private static string OwnerAddress(Guid id) => DurableRepository.GetAddress("peer:persistence-routing", "owner:" + id.ToString("N"));
}
