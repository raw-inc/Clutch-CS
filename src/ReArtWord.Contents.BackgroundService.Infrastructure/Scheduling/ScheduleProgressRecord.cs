using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;

/// <summary>進行位置と、その内容を共有した操作の確認条件を保存します。</summary>
/// <param name="NodeId">時間指定ノード。</param>
/// <param name="Cursor">時間指定の基準と受付済み位置。</param>
/// <param name="OperationId">この記録を確定した操作。</param>
/// <param name="ConfirmedPeers">共有時に固定した確認対象。</param>
internal sealed record ScheduleProgressRecord(string NodeId, ScheduleCursor Cursor, Guid OperationId, IReadOnlyList<PeerInfo> ConfirmedPeers);
