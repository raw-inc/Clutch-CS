using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

/// <summary>確認済み値と候補を混同せず有限キャッシュへ保持します。</summary>
/// <param name="State">値を含まない表示状態です。</param>
/// <param name="Confirmed">最後のprimary確認値です。</param>
/// <param name="Effective">現在実際に利用している値です。</param>
/// <param name="Candidate">明示された候補です。</param>
/// <param name="OperationId">候補を作成した元操作です。</param>
/// <param name="PersistenceOutcome">候補保存の実行結果です。</param>
/// <param name="Active">候補が現在利用中かです。</param>
/// <param name="Fallback">保存した候補の代替slotです。</param>
/// <param name="ApplyUnconfirmed">採用開始後の結果を確認できず再要求を止めているかです。</param>
/// <param name="ReconciliationResolved">元比較書込みが確定し、新規編集まで再送しないかです。</param>
internal sealed record AvailabilityEntry(AvailabilityState State, NodeValue? Confirmed, NodeValue? Effective = null, NodeValue? Candidate = null, Guid OperationId = default, EOutcome PersistenceOutcome = EOutcome.Succeeded, bool Active = false, AvailabilityFallbackPointer? Fallback = null, bool ApplyUnconfirmed = false, bool ReconciliationResolved = false);
