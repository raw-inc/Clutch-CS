using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>共有対象の実値と変更履歴です。</summary>
/// <param name="Entries">現在の値。</param>
/// <param name="Receipts">変更の確定結果。</param>
/// <param name="OperationOwners">空結果を含む操作の論理範囲。旧単一媒体では省略できます。</param>
/// <param name="CausalEntries">共有現在版と競合候補に対応する因果情報です。</param>
/// <param name="LegacyEntries">移行前の本文を持たない祖先索引です。</param>
/// <param name="HistoryBoundary">圧縮と削除を実施した区間です。</param>
/// <param name="RetiredOperations">削除した旧共有操作の再実行を防ぐ索引です。</param>
/// <param name="ProtectedHistory">現在の復旧に必要で、保持境界より前でも送る履歴版です。</param>
/// <param name="RetiredKeys">本文の同期を終えた一回限りの共有受付です。</param>
/// <param name="PendingOperations">再実行や予約の作成には使わず、同期要否だけを照合する未確定位置です。</param>
/// <param name="DeferredHistory">段階同期で、本文を背景取得する確定済み操作と過去保存版の存在証明です。</param>
/// <param name="DeferredSourcePeer">背景本文の取得元です。受信時に通信上の送信Peerと照合します。</param>
/// <param name="RecoveredHistory">背景転送だけで使用する、有限の元本文です。</param>
public sealed record RepositorySnapshot(IReadOnlyList<DurableEntry> Entries, IReadOnlyList<DurableReceipt> Receipts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PersistenceOperationOwner>? OperationOwners = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RepositoryCausalEntry>? CausalEntries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RepositoryLegacyEntry>? LegacyEntries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoryRetentionBoundary? HistoryBoundary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RetiredOperation>? RetiredOperations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ProtectedHistory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RetiredHistoryKey>? RetiredKeys = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Guid>? PendingOperations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Recovery.RecoveryRecord>? DeferredHistory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeferredSourcePeer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Recovery.RecoveryRecordBody>? RecoveredHistory = null);
