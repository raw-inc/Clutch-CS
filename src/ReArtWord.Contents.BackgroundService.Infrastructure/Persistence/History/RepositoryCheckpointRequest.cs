using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>保持範囲外からの状態取込みを、新しい復旧基準点として記録します。</summary>
internal static class RepositoryCheckpointRequest
{
    internal static string Key => "checkpoint-required";

    /// <summary>取込みと同じ取引で、再起動後にも残る取得要求を保存します。</summary>
    internal static void Set(RepositoryDocument document, bool required)
    {
        NodeValue value = NodeValue.FromObject(required);
        document.Entries[DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, Key)] = new(RepositoryHistoryGuard.Scope, Key, ContractJson.Hash(value), value);
    }

    /// <summary>直近の取込みで基準点が必要になったか確認します。</summary>
    /// <returns>未処理の取得要求があるかです。</returns>
    internal static bool Required(RepositoryDocument document)
        => document.Entries.GetValueOrDefault(DurableRepository.GetAddress(RepositoryHistoryGuard.Scope, Key))?.Value?.Json.ValueKind == System.Text.Json.JsonValueKind.True;
}
