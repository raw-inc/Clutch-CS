using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>開始記録と準備を、共有コミットの成功とは分けて返します。</summary>
/// <param name="Initialized">元の固定参加者を保存した結果です。</param>
/// <param name="Prepared">一括保存した準備結果です。nullの場合、開始記録が成功していれば通常の準備へ進みます。</param>
/// <param name="Mutations">全固定対象へ渡す同一の変更候補です。</param>
internal sealed record ReplicationStartResult(DurableReceipt Initialized, DurableReceipt? Prepared, IReadOnlyList<DurableMutation> Mutations);
