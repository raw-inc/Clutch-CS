using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;

/// <summary>元操作のローカル保存先と、保存前拒否結果を固定します。</summary>
/// <param name="Operation">他Peerへも渡せる論理範囲です。</param>
/// <param name="Backend">この保存領域内だけの物理媒体です。</param>
/// <param name="Result">跨媒体等で変更前に拒否した確定結果です。</param>
internal sealed record RoutingOwner(PersistenceOperationOwner Operation, EStorageBackend Backend, DurableReceipt? Result = null);
